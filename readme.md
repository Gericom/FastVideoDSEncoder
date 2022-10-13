FastVideoDS Encoder
===================
Encoder for the FastVideoDS format. Use [FastVideoDS Player](https://github.com/Gericom/FastVideoDSPlayer) to play back the encoded videos.

## Usage
    FastVideoDSEncoder [-j jobs] input output.fv

* **-j *jobs*** Number of concurrent jobs (optional, default: cpu threads / 1.5)
* ***input*** The input video file. Most formats are supported through FFmpeg.
* ***output.fv*** The output video file.

## Libraries Used
* [CommandLineParser](https://github.com/commandlineparser/commandline)
* [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)
* [FFmpeg](https://ffmpeg.org/)