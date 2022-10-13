using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Gericom.FastVideoDS.Frames;
using static FFmpeg.AutoGen.ffmpeg;

namespace Gericom.FastVideoDSEncoder
{
    public unsafe class FFMpegDecoder : IDisposable
    {
        public const int StreamAuto = -1;
        public const int NoStream   = -2;

        private AVFormatContext* _formatContext;
        private AVCodecContext*  _videoDecContext;
        private AVCodecContext*  _audioDecContext;
        private SwsContext*      _swsContext;
        private SwrContext*      _swrContext;
        private AVPacket*        _packet;
        private AVFrame*         _frame;

        private readonly byte* _rgbData;

        private readonly Queue<RefFrame> _frameQueue = new();
        private readonly FramePool       _framePool;
        private readonly short[]         _leftBuffer  = new short[2 * 48000];
        private readonly short[]         _rightBuffer = new short[2 * 48000];
        private          int             _audioSampleCount;
        private          int             _audioReadOffset;
        private          int             _audioWriteOffset;

        private long _curVideoPts = -1;
        private long _curAudioPts = -1;

        public long FirstVideoPts    { get; private set; } = -1;
        public long FirstAudioPktPos { get; private set; } = -1;

        public long MaxAudioPktPos { get; set; } = -1;

        public int VideoStreamId { get; }
        public int AudioStreamId { get; }

        public int FrameHeight { get; private set; }

        static FFMpegDecoder()
        {
            ffmpeg.RootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64");
        }

        public FFMpegDecoder(string srcPath, long startPts = 0, int videoStreamId = -1, int audioStreamId = -1)
        {
            AVFormatContext* formatContext = null;
            if (avformat_open_input(&formatContext, srcPath, null, null) < 0)
                throw new Exception("avformat_open_input error");

            _formatContext = formatContext;

            if (avformat_find_stream_info(_formatContext, null) < 0)
                throw new Exception("avformat_find_stream_info error");

            if (videoStreamId >= 0 &&
                (videoStreamId >= formatContext->nb_streams ||
                 formatContext->streams[videoStreamId]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO))
                throw new ArgumentException(nameof(videoStreamId));

            if (audioStreamId >= 0 &&
                (audioStreamId >= formatContext->nb_streams ||
                 formatContext->streams[audioStreamId]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO))
                throw new ArgumentException(nameof(audioStreamId));

            for (int i = 0; i < formatContext->nb_streams; i++)
            {
                var codecType = formatContext->streams[i]->codecpar->codec_type;

                if (videoStreamId != NoStream && videoStreamId < 0 && codecType == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamId = i;
                    continue;
                }

                if (audioStreamId != NoStream && audioStreamId < 0 && codecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioStreamId = i;
                    continue;
                }
            }

            if (videoStreamId != NoStream && videoStreamId < 0)
                throw new Exception("No video stream found");

            if (audioStreamId != NoStream && audioStreamId < 0)
                throw new Exception("No audio stream found");

            VideoStreamId = videoStreamId;
            AudioStreamId = audioStreamId;

            if (VideoStreamId != NoStream)
            {
                AVCodec* videoDec = avcodec_find_decoder(_formatContext->streams[VideoStreamId]->codecpar->codec_id);
                _videoDecContext = avcodec_alloc_context3(videoDec);
                avcodec_parameters_to_context(_videoDecContext, _formatContext->streams[VideoStreamId]->codecpar);
                avcodec_open2(_videoDecContext, videoDec, null);
            }

            if (AudioStreamId != NoStream)
            {
                AVCodec* audioDec = avcodec_find_decoder(_formatContext->streams[AudioStreamId]->codecpar->codec_id);
                _audioDecContext = avcodec_alloc_context3(audioDec);
                avcodec_parameters_to_context(_audioDecContext, _formatContext->streams[AudioStreamId]->codecpar);
                avcodec_open2(_audioDecContext, audioDec, null);
            }

            if (startPts != 0 && VideoStreamId != NoStream)
            {
                if (av_seek_frame(_formatContext, VideoStreamId, startPts, 0) < 0)
                    throw new Exception("av_seek_frame failed");
                avcodec_flush_buffers(_videoDecContext);

                if (AudioStreamId != NoStream)
                    avcodec_flush_buffers(_audioDecContext);
            }

            if (VideoStreamId != NoStream)
            {
                var aspect = _videoDecContext->sample_aspect_ratio;
                if (aspect.num == 0 && aspect.den == 1)
                    aspect.num = 1;

                FrameHeight = (int)Math.Round(_videoDecContext->height * 256.0 / _videoDecContext->width * aspect.den /
                                              aspect.num);

                int mod8 = FrameHeight & 7;
                if (mod8 != 0)
                {
                    FrameHeight &= ~7;
                    if (mod8 >= 4)
                        FrameHeight += 8;
                }

                _framePool = new FramePool(256, FrameHeight);

                _swsContext = sws_getContext(_videoDecContext->width, _videoDecContext->height,
                    _videoDecContext->pix_fmt,
                    256, FrameHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                    SWS_LANCZOS | SWS_FULL_CHR_H_INT | SWS_FULL_CHR_H_INP | SWS_ACCURATE_RND, null, null, null);
            }

            _packet = av_packet_alloc();
            _frame  = av_frame_alloc();

            _audioSampleCount = 0;
            _audioReadOffset  = 0;
            _audioWriteOffset = 0;

            if (VideoStreamId != NoStream)
            {
                _rgbData = (byte*)NativeMemory.AlignedAlloc(256 * 192 * 4, 16);

                while (FirstVideoPts == -1 && PumpData()) ;
            }

            if (AudioStreamId != NoStream)
            {
                while (FirstAudioPktPos == -1 && PumpData()) ;
            }
        }

        public AVRational GetFrameRate()
        {
            var rate = _formatContext->streams[VideoStreamId]->r_frame_rate;
            // if (rate.num == 50 && rate.den == 1 && _formatContext->streams[VideoStreamId]->codecpar->field_order !=
            // AVFieldOrder.AV_FIELD_PROGRESSIVE)
            // rate.num = 25;
            return rate;
            // return _formatContext->streams[VideoStreamId]->r_frame_rate;
        }

        public AVRational GetVideoTimeBase()
        {
            return _formatContext->streams[VideoStreamId]->time_base;
        }

        public long GetDuration()
        {
            if (_formatContext->streams[VideoStreamId]->duration <= 0)
                return _formatContext->duration * _formatContext->streams[VideoStreamId]->time_base.den /
                       ((long)AV_TIME_BASE * _formatContext->streams[VideoStreamId]->time_base.num);

            return _formatContext->streams[VideoStreamId]->duration;
        }

        public int GetAudioRate()
        {
            return 47605; //_formatContext->streams[AudioStreamId]->codecpar->sample_rate;
        }

        private bool ReceiveVideoFrame()
        {
            if (avcodec_receive_frame(_videoDecContext, _frame) < 0)
                return false;

            if (_frame->best_effort_timestamp <= _curVideoPts)
            {
                av_frame_unref(_frame);
                return false;
            }

            if (FirstVideoPts == -1)
                FirstVideoPts = _frame->best_effort_timestamp;

            _curVideoPts = _frame->best_effort_timestamp;

            // fixed (byte* pRgb = _rgbData)
            // {
            sws_scale(_swsContext, _frame->data, _frame->linesize, 0,
                _videoDecContext->height, new[] { _rgbData }, new[] { 256 * 4 });

            var refFrame = _framePool.AcquireFrame();

            refFrame.Frame.FromRgba32(_rgbData, 256 * 4);
            _frameQueue.Enqueue(refFrame);
            // if(_frame->interlaced_frame != 0)
            //     _frameQueue.Enqueue(RGB555Frame.FromRgba32(pRgb, 256, FrameHeight, 256 * 4));
            // }

            av_frame_unref(_frame);
            return true;
        }

        private bool ReceiveAudioFrame()
        {
            if (avcodec_receive_frame(_audioDecContext, _frame) < 0)
                return false;

            if (_frame->best_effort_timestamp <= _curAudioPts)
            {
                av_frame_unref(_frame);
                return false;
            }

            if (MaxAudioPktPos != -1 && _frame->pkt_pos >= MaxAudioPktPos)
            {
                av_frame_unref(_frame);
                return false;
            }

            if (FirstAudioPktPos == -1)
                FirstAudioPktPos = _frame->pkt_pos;

            _curAudioPts = _frame->best_effort_timestamp;

            if (_swrContext == null)
            {
                var             inChLayout = &_frame->ch_layout;
                AVChannelLayout outChLayout;
                av_channel_layout_from_mask(&outChLayout, AV_CH_LAYOUT_STEREO);
                // if (chLayout == 0)
                //     chLayout = av_get_default_channel_layout(_frame->channels);
                fixed (SwrContext** swr = &_swrContext)
                {
                    int result = swr_alloc_set_opts2(swr,
                        &outChLayout, AVSampleFormat.AV_SAMPLE_FMT_S16P, 47605,
                        inChLayout, _audioDecContext->sample_fmt, _frame->sample_rate,
                        0, null);
                    if (result < 0)
                        throw new Exception("swr_alloc_set_opts2 error");
                }

                swr_init(_swrContext);
            }

            byte** outBufs = stackalloc byte*[2];
            int outSamples = (int)av_rescale_rnd(
                swr_get_delay(_swrContext, _frame->sample_rate) + _frame->nb_samples, 47605,
                _frame->sample_rate, AVRounding.AV_ROUND_UP);

            if (_audioWriteOffset + outSamples <= _leftBuffer.Length)
            {
                fixed (short* leftPtr = _leftBuffer, rightPtr = _rightBuffer)
                {
                    outBufs[0] = (byte*)&leftPtr[_audioWriteOffset];
                    outBufs[1] = (byte*)&rightPtr[_audioWriteOffset];
                    outSamples = swr_convert(_swrContext, outBufs, outSamples, (byte**)&_frame->data,
                        _frame->nb_samples);
                    if (outSamples < 0)
                        throw new Exception("swr_convert error");
                }
            }
            else
            {
                var leftBuf  = new short[outSamples];
                var rightBuf = new short[outSamples];
                fixed (short* leftPtr = leftBuf, rightPtr = rightBuf)
                {
                    outBufs[0] = (byte*)leftPtr;
                    outBufs[1] = (byte*)rightPtr;
                    outSamples = swr_convert(_swrContext, outBufs, outSamples, (byte**)&_frame->data,
                        _frame->nb_samples);
                    if (outSamples < 0)
                        throw new Exception("swr_convert error");
                }

                int firstHalf = _leftBuffer.Length - _audioWriteOffset;
                if (firstHalf > outSamples)
                    firstHalf = outSamples;
                Array.Copy(leftBuf, 0, _leftBuffer, _audioWriteOffset, firstHalf);
                Array.Copy(rightBuf, 0, _rightBuffer, _audioWriteOffset, firstHalf);
                if (firstHalf != outSamples)
                {
                    int secondHalf = outSamples - firstHalf;
                    Array.Copy(leftBuf, firstHalf, _leftBuffer, 0, secondHalf);
                    Array.Copy(rightBuf, firstHalf, _rightBuffer, 0, secondHalf);
                }
            }

            _audioWriteOffset += outSamples;
            if (_audioWriteOffset >= _leftBuffer.Length)
                _audioWriteOffset -= _leftBuffer.Length;

            _audioSampleCount += outSamples;
            if (_audioSampleCount > _leftBuffer.Length)
                throw new Exception("Audio buffer overflow!");

            av_frame_unref(_frame);
            return true;
        }

        private bool PumpData()
        {
            int ret = av_read_frame(_formatContext, _packet);
            if (ret == AVERROR_EOF)
            {
                if (VideoStreamId != NoStream)
                {
                    while (ReceiveVideoFrame()) ;
                }

                if (AudioStreamId != NoStream)
                {
                    while (ReceiveAudioFrame()) ;
                }

                return false;
            }

            if (_packet->stream_index == VideoStreamId)
            {
                avcodec_send_packet(_videoDecContext, _packet);
                ReceiveVideoFrame();
            }
            else if (_packet->stream_index == AudioStreamId)
            {
                avcodec_send_packet(_audioDecContext, _packet);
                ReceiveAudioFrame();
            }

            av_packet_unref(_packet);

            return true;
        }

        public RefFrame GetNextFrame()
        {
            if (VideoStreamId == NoStream)
                return null;

            while (_frameQueue.Count == 0)
            {
                if (!PumpData())
                {
                    if (_frameQueue.Count == 0)
                        return null;
                    break;
                }
            }

            return _frameQueue.Dequeue();
        }

        public int GetAudioSamples(short[] leftDst, short[] rightDst, int count)
        {
            if (AudioStreamId == NoStream)
                return 0;

            if (count > _leftBuffer.Length)
                throw new Exception("Requesting too much audio data");

            while (_audioSampleCount < count)
            {
                if (!PumpData())
                {
                    count = _audioSampleCount;
                    break;
                }
            }

            if (_audioReadOffset + count <= _leftBuffer.Length)
            {
                Array.Copy(_leftBuffer, _audioReadOffset, leftDst, 0, count);
                Array.Copy(_rightBuffer, _audioReadOffset, rightDst, 0, count);
            }
            else
            {
                int firstHalf = _leftBuffer.Length - _audioReadOffset;
                Array.Copy(_leftBuffer, _audioReadOffset, leftDst, 0, firstHalf);
                Array.Copy(_rightBuffer, _audioReadOffset, rightDst, 0, firstHalf);
                int secondHalf = count - firstHalf;
                Array.Copy(_leftBuffer, 0, leftDst, firstHalf, secondHalf);
                Array.Copy(_rightBuffer, 0, rightDst, firstHalf, secondHalf);
            }

            _audioReadOffset += count;
            if (_audioReadOffset >= _leftBuffer.Length)
                _audioReadOffset -= _leftBuffer.Length;
            _audioSampleCount -= count;
            return count;
        }

        public void Dispose()
        {
            fixed (AVPacket** packet = &_packet)
                av_packet_free(packet);
            fixed (AVFrame** frame = &_frame)
                av_frame_free(frame);
            if (VideoStreamId != NoStream)
            {
                sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (AudioStreamId != NoStream && _swrContext != null)
            {
                fixed (SwrContext** swrContext = &_swrContext)
                    swr_free(swrContext);
            }

            if (AudioStreamId != NoStream)
            {
                avcodec_close(_audioDecContext);
                fixed (AVCodecContext** audioDecContext = &_audioDecContext)
                    avcodec_free_context(audioDecContext);
            }

            if (VideoStreamId != NoStream)
            {
                avcodec_close(_videoDecContext);
                fixed (AVCodecContext** videoDecContext = &_videoDecContext)
                    avcodec_free_context(videoDecContext);
            }

            fixed (AVFormatContext** formatContext = &_formatContext)
                avformat_close_input(formatContext);

            if (_rgbData != null)
                NativeMemory.AlignedFree(_rgbData);

            GC.SuppressFinalize(this);
        }

        ~FFMpegDecoder()
        {
            if (_rgbData != null)
                NativeMemory.AlignedFree(_rgbData);
        }
    }
}