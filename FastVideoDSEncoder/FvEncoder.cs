using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace Gericom.FastVideoDSEncoder
{
    public class FvEncoder
    {
        private const int AudioFrameSize = 256;

        public volatile int[] JobProgess;

        public List<(int frame, uint offset)>[] JobKeyFrames;

        public uint[] JobFileSize;

        public int MaxGopLength = 250;

        private FvEncoder() { }

        private record EncoderParams(int JobId, string DestinationPath, FFMpegDecoder Decoder, int StartFrame,
            int EndFrame);

        private void EncoderThreadMain(object encParams)
        {
            var (jobId, dstPath, decoder, frame, endFrame) = (EncoderParams)encParams;

            var dummyAudioBuf = new byte[4 + (AudioFrameSize / 2)];
            using (var outStream = File.Create(dstPath))
            {
                int audioRate = decoder.GetAudioRate();
                var videoRate = decoder.GetFrameRate();
                var encoder = new Gericom.FastVideoDS.FastVideoDSEncoder(256, decoder.FrameHeight, 30, 0, MaxGopLength);

                int inFrame  = frame;
                int outFrame = frame;

                while (true)
                {
                    if (inFrame < endFrame)
                    {
                        var frameData = decoder.GetNextFrame();
                        if (frameData != null)
                        {
                            encoder.SendFrame(frameData);
                            if (++inFrame == endFrame)
                                encoder.Flush();
                        }
                        else
                        {
                            inFrame = endFrame;
                            encoder.Flush();
                        }
                    }

                    var encData = encoder.ReceiveFrame();
                    if (encData != null)
                    {
                        long expectedSamples = (long)audioRate * (outFrame + 1) * videoRate.den / videoRate.num;
                        long audioSamplesWritten = ((long)audioRate * outFrame * videoRate.den / videoRate.num) /
                            AudioFrameSize * AudioFrameSize;
                        int newSamples  = (int)(expectedSamples - audioSamplesWritten);
                        int audioFrames = newSamples > 0 ? newSamples / AudioFrameSize : 0;

                        long curPos = outStream.Position;

                        int  frameLen  = (encData.Data.Length + 3) & ~3;
                        uint sizeField = ((uint)frameLen & 0x1FFFFu) | ((uint)audioFrames << 17);
                        outStream.WriteByte((byte)(sizeField & 0xFF));
                        outStream.WriteByte((byte)((sizeField >> 8) & 0xFF));
                        outStream.WriteByte((byte)((sizeField >> 16) & 0xFF));
                        outStream.WriteByte((byte)((sizeField >> 24) & 0xFF));
                        outStream.Write(encData.Data);
                        for (int i = encData.Data.Length; i < frameLen; i++)
                            outStream.WriteByte(0);

                        for (int j = 0; j < audioFrames; j++)
                        {
                            outStream.Write(dummyAudioBuf);
                            outStream.Write(dummyAudioBuf);
                        }

                        if (encData.Type == Gericom.FastVideoDS.FastVideoDSEncoder.FvFrameType.IFrame)
                            JobKeyFrames[jobId].Add((outFrame, (uint)curPos));

                        outFrame++;

                        JobProgess[jobId] = outFrame;
                    }

                    if (inFrame == endFrame && encoder.FrameQueueEmpty)
                        break;
                }

                decoder.Dispose();

                JobFileSize[jobId] = (uint)outStream.Length;
            }
        }

        public static void Encode(string inFile, string outFile, int jobCount = 1)
        {
            if (!Avx2.IsSupported)
                throw new Exception("Avx2 instruction support not available");

            if (jobCount < 1)
                throw new ArgumentException(nameof(jobCount));

            var context = new FvEncoder();

            var decoder = new FFMpegDecoder(inFile);

            int audioStream = decoder.AudioStreamId;

            long duration = decoder.GetDuration();

            var timeBase    = decoder.GetVideoTimeBase();
            var videoRate   = decoder.GetFrameRate();
            int audioRate   = decoder.GetAudioRate();
            int frameHeight = decoder.FrameHeight;

            decoder.Dispose();

            long partDuration = duration / jobCount;

            var decoders = new FFMpegDecoder[jobCount];

            for (int i = 0; i < jobCount; i++)
            {
                decoders[i] = new FFMpegDecoder(inFile, partDuration * i, decoder.VideoStreamId,
                    FFMpegDecoder.NoStream); //decoder.AudioStreamId);
                // if (i != 0)
                //     decoders[i - 1].MaxAudioPktPos = decoders[i].FirstAudioPktPos;
            }

            int ptsToFrame(long pts) =>
                (int)(pts * timeBase.num * videoRate.num / ((long)timeBase.den * videoRate.den));

            context.JobProgess   = new int[jobCount];
            context.JobKeyFrames = new List<(int, uint)>[jobCount];
            context.JobFileSize  = new uint[jobCount];
            // var jobProgess = new int[jobCount];
            var startFrames = new int[jobCount];
            var endFrames   = new int[jobCount];

            var tasks = new Task[jobCount];
            for (int i = 0; i < jobCount; i++)
            {
                int startFrame = i == 0 ? 0 : ptsToFrame(decoders[i].FirstVideoPts);
                int endFrame   = i == jobCount - 1 ? ptsToFrame(duration) : ptsToFrame(decoders[i + 1].FirstVideoPts);

                startFrames[i]          = startFrame;
                endFrames[i]            = endFrame;
                context.JobProgess[i]   = startFrame;
                context.JobKeyFrames[i] = new();

                tasks[i] = Task.Factory.StartNew(context.EncoderThreadMain,
                    new EncoderParams(i, $"{outFile}.{i}", decoders[i], startFrame, endFrame));
            }

            Console.WriteLine($"Encoding video with {jobCount} jobs ...");
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                bool allDone = true;
                for (int i = 0; i < jobCount; i++)
                {
                    if (!tasks[i].IsCompleted)
                    {
                        allDone = false;
                        break;
                    }
                }

                int consoleWidth = Console.BufferWidth;
                int barWidth     = consoleWidth - 3;
                Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                Console.Write('[');
                int totalFrames = endFrames[^1];
                for (int i = 0; i < barWidth; i++)
                {
                    int barFrame = (i + 1) * totalFrames / barWidth;
                    for (int j = 0; j < jobCount; j++)
                    {
                        if (barFrame >= startFrames[j] && barFrame <= endFrames[j])
                        {
                            if (context.JobProgess[j] >= barFrame)
                                Console.Write('#');
                            else
                                Console.Write('.');
                            break;
                        }
                    }
                }

                Console.Write(']');

                if (allDone)
                {
                    Console.WriteLine();
                    break;
                }

                Thread.Sleep(500);
            }

            uint keyFrameCount = (uint)context.JobKeyFrames.Sum(j => (uint)j.Count);

            var audioBlockL = new short[AudioFrameSize];
            var audioBlockR = new short[AudioFrameSize];

            using (var outStream = File.Create(outFile))
            {
                var header = new byte[0x1C + keyFrameCount * 8];
                header[0] = (byte)'F';
                header[1] = (byte)'V';
                header[2] = (byte)'D';
                header[3] = (byte)'S';
                var headerSpan = header.AsSpan();
                headerSpan.WriteLe<ushort>(0x04, 256);
                headerSpan.WriteLe<ushort>(0x06, (ushort)frameHeight);
                headerSpan.WriteLe<uint>(0x08, (uint)videoRate.num);
                headerSpan.WriteLe<uint>(0x0C, (uint)videoRate.den);
                headerSpan.WriteLe<ushort>(0x10, (ushort)audioRate);
                headerSpan.WriteLe<ushort>(0x12, 2);
                headerSpan.WriteLe<uint>(0x14, (uint)endFrames[^1]);
                headerSpan.WriteLe<uint>(0x18, keyFrameCount);

                uint jobOffset    = (uint)header.Length;
                int  headerOffset = 0x1C;
                for (int i = 0; i < jobCount; i++)
                {
                    foreach (var (frame, offset) in context.JobKeyFrames[i])
                    {
                        headerSpan.WriteLe<uint>(headerOffset, (uint)frame);
                        headerSpan.WriteLe<uint>(headerOffset + 4, jobOffset + offset);
                        headerOffset += 8;
                    }

                    jobOffset += context.JobFileSize[i];
                }

                outStream.Write(header);

                for (int i = 0; i < jobCount; i++)
                {
                    using (var partStream = File.OpenRead($"{outFile}.{i}"))
                        partStream.CopyTo(outStream);

                    File.Delete($"{outFile}.{i}");
                }

                stopwatch.Stop();
                Console.WriteLine($"Video encoding done in {stopwatch.Elapsed}");

                Console.WriteLine("Encoding audio...");

                stopwatch = Stopwatch.StartNew();

                var audioDec = new FFMpegDecoder(inFile, 0, FFMpegDecoder.NoStream, audioStream);

                outStream.Position = header.Length;
                var sizeBuf = new byte[4];

                int f = 0;
                var audioTask = Task.Factory.StartNew(() =>
                {
                    Adpcm.AdpcmState lastLeft  = null;
                    Adpcm.AdpcmState lastRight = null;
                    for (; f < endFrames[^1]; f++)
                    {
                        outStream.Read(sizeBuf);
                        uint sizeField = BinaryPrimitives.ReadUInt32LittleEndian(sizeBuf);

                        uint frameLen    = sizeField & 0x1FFFFu;
                        uint audioFrames = sizeField >> 17;

                        outStream.Position += frameLen;

                        for (int j = 0; j < audioFrames; j++)
                        {
                            Array.Clear(audioBlockL, 0, audioBlockL.Length);
                            Array.Clear(audioBlockR, 0, audioBlockR.Length);
                            int samples = audioDec.GetAudioSamples(audioBlockL, audioBlockR, AudioFrameSize);
                            (var data, lastLeft) = Adpcm.Encode(audioBlockL, lastLeft, true);
                            outStream.Write(data);

                            (data, lastRight) = Adpcm.Encode(audioBlockR, lastRight, true);
                            outStream.Write(data);
                        }
                    }
                });

                while (!audioTask.IsCompleted)
                {
                    int consoleWidth = Console.BufferWidth;
                    int barWidth     = consoleWidth - 3;
                    Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                    Console.Write('[');
                    int totalFrames = endFrames[^1];
                    int done        = barWidth * f / totalFrames;
                    Console.Write(Enumerable.Repeat('#', done).ToArray());
                    Console.Write(Enumerable.Repeat('.', barWidth - done).ToArray());

                    Console.Write(']');

                    Thread.Sleep(500);
                }

                Console.WriteLine();

                stopwatch.Stop();
                Console.WriteLine($"Audio encoding done in {stopwatch.Elapsed}");
            }
        }
    }
}