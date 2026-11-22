using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Reading ffprobe's JSON. Everything the merge decides rests on this, and
/// ffprobe prints most numbers as strings, some as numbers, and some not at all.
public class MediaProbeTests
{
    private const string Path = @"C:\clips\a.mp4";

    [Fact]
    public void ReadsATypicalFile()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p",
              "r_frame_rate": "30/1", "time_base": "1/30000",
              "bit_rate": "8000000", "duration": "10.000000" },
            { "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "sample_rate": "48000", "channels": 2, "channel_layout": "stereo",
              "duration": "10.016000" }
          ],
          "format": { "duration": "10.016000", "bit_rate": "8200000" }
        }
        """;

        var probe = MediaProbe.Parse(json, Path)!;

        Assert.Equal("a.mp4", probe.FileName);
        Assert.Equal(10.016, probe.Duration, 3);

        Assert.Equal(new VideoFormat("h264", "High", 1920, 1080, "yuv420p", "30/1"), probe.Video);
        Assert.Equal("1/30000", probe.VideoTimeBase);
        Assert.Equal(8_000_000, probe.VideoBitRate);
        Assert.Equal(10.0, probe.VideoDuration!.Value, 3);

        Assert.Equal(new AudioFormat("aac", "LC", 48000, 2, "stereo"), probe.Audio);
        Assert.Equal(10.016, probe.AudioDuration!.Value, 3);
    }

    [Fact]
    public void SkipsCoverArtDisguisedAsAVideoStream()
    {
        // An embedded thumbnail is a one-frame video stream, and taking it as
        // the picture format would conform every file to a 600x600 still.
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "mjpeg", "profile": "",
              "width": 600, "height": 600, "pix_fmt": "yuvj420p",
              "r_frame_rate": "90000/1", "disposition": { "attached_pic": 1 } },
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p",
              "r_frame_rate": "30/1", "time_base": "1/30000" }
          ],
          "format": { "duration": "5.0" }
        }
        """;

        var probe = MediaProbe.Parse(json, Path)!;

        Assert.Equal("h264", probe.Video!.Codec);
        Assert.Equal(1920, probe.Video.Width);
    }

    [Fact]
    public void KeepsARealVideoStreamThatCarriesADisposition()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p",
              "r_frame_rate": "30/1", "disposition": { "attached_pic": 0 } }
          ],
          "format": { "duration": "5.0" }
        }
        """;

        Assert.Equal("h264", MediaProbe.Parse(json, Path)!.Video!.Codec);
    }

    [Fact]
    public void TakesANumberWhetherItIsQuotedOrNot()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": "1920", "height": "1080", "pix_fmt": "yuv420p",
              "r_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "sample_rate": 48000, "channels": "2", "channel_layout": "stereo" }
          ],
          "format": { "duration": 7.5 }
        }
        """;

        var probe = MediaProbe.Parse(json, Path)!;

        Assert.Equal(1920, probe.Video!.Width);
        Assert.Equal(1080, probe.Video.Height);
        Assert.Equal(48000, probe.Audio!.SampleRate);
        Assert.Equal(2, probe.Audio.Channels);
        Assert.Equal(7.5, probe.Duration, 3);
    }

    [Fact]
    public void NamesTheChannelLayoutFfprobeLeftOut()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "sample_rate": "48000", "channels": 6 }
          ],
          "format": { "duration": "5.0" }
        }
        """;

        Assert.Equal("5.1", MediaProbe.Parse(json, Path)!.Audio!.ChannelLayout);
    }

    [Fact]
    public void EstimatesTheVideoRateFromTheContainerWhenTheStreamHasNone()
    {
        // Without a per-stream figure, the container rate less a typical audio
        // track is close enough to size a re-encode.
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "r_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "sample_rate": "48000", "channels": 2, "channel_layout": "stereo" }
          ],
          "format": { "duration": "5.0", "bit_rate": "1000000" }
        }
        """;

        Assert.Equal(808_000, MediaProbe.Parse(json, Path)!.VideoBitRate);
    }

    [Fact]
    public void TakesTheWholeContainerRateWhenThereIsNoAudio()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "r_frame_rate": "30/1" }
          ],
          "format": { "duration": "5.0", "bit_rate": "1000000" }
        }
        """;

        Assert.Equal(1_000_000, MediaProbe.Parse(json, Path)!.VideoBitRate);
    }

    [Fact]
    public void NeverEstimatesANegativeVideoRate()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 320, "height": 240, "pix_fmt": "yuv420p", "r_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "aac", "profile": "LC",
              "sample_rate": "48000", "channels": 2, "channel_layout": "stereo" }
          ],
          "format": { "duration": "5.0", "bit_rate": "100000" }
        }
        """;

        Assert.Equal(0, MediaProbe.Parse(json, Path)!.VideoBitRate);
    }

    [Fact]
    public void ReadsAFileWithNoAudioTrack()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "profile": "High",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "r_frame_rate": "30/1" }
          ],
          "format": { "duration": "5.0" }
        }
        """;

        var probe = MediaProbe.Parse(json, Path)!;

        Assert.NotNull(probe.Video);
        Assert.Null(probe.Audio);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"streams\": ")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void ReturnsNullRatherThanThrowingOnOutputItCannotRead(string json)
    {
        Assert.Null(MediaProbe.Parse(json, Path));
    }

    [Fact]
    public void ReadsAnEmptyObjectAsAFileWithNothingInIt()
    {
        var probe = MediaProbe.Parse("{}", Path)!;

        Assert.NotNull(probe);
        Assert.Null(probe.Video);
        Assert.Null(probe.Audio);
        Assert.Equal(0, probe.Duration);
    }

    [Fact]
    public void AsksFfprobeForJsonAndPutsThePathLast()
    {
        var arguments = MediaProbe.FfprobeArguments(Path);

        Assert.Equal(Path, arguments[^1]);
        Assert.Contains("-of", arguments);
        Assert.Contains("json", arguments);
    }
}
