using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Reading which build of ffmpeg is in use.
///
/// The download comes from a release tag whose contents are replaced in place,
/// so once the tag moves on there is no way to tell which binary a failure came
/// from. That is why the NVENC freeze of 2026-11-08 could not be investigated.
public class FfmpegVersionTests
{
    // Both banners are copied from the builds on this machine (2026-11-22).
    private const string GitBuild =
        "ffmpeg version N-129166-gae9f4540cb-20261121 Copyright (c) 2000-2026 the FFmpeg developers\n" +
        "  built with gcc 16.2.0 (crosstool-NG 1.29.0.36_fbb6372)\n" +
        "  configuration: --prefix=/ffbuild/prefix --enable-gpl\n";

    private const string ReleaseBuild =
        "ffmpeg version 8.0.1-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers\n" +
        "  built with gcc 15.2.0 (Rev2, Built by MSYS2 project)\n";

    [Fact]
    public void ReadsTheBannerOffAGitBuild()
    {
        Assert.Equal(
            "ffmpeg version N-129166-gae9f4540cb-20261121 Copyright (c) 2000-2026 the FFmpeg developers",
            FfmpegVersion.FromBanner(GitBuild));
    }

    [Fact]
    public void ReadsTheBuildNameOffAGitBuild()
    {
        Assert.Equal("N-129166-gae9f4540cb-20261121", FfmpegVersion.BuildOf(GitBuild));
    }

    [Fact]
    public void ReadsTheBuildNameOffAReleaseBuild()
    {
        Assert.Equal("8.0.1-essentials_build-www.gyan.dev", FfmpegVersion.BuildOf(ReleaseBuild));
    }

    [Fact]
    public void ReadsFfprobeToo()
    {
        const string banner =
            "ffprobe version N-129166-gae9f4540cb-20261121 Copyright (c) 2007-2026 the FFmpeg developers\n";

        Assert.Equal("N-129166-gae9f4540cb-20261121", FfmpegVersion.BuildOf(banner));
    }

    [Fact]
    public void FindsTheBannerEvenWhenSomethingElseCameFirst()
    {
        // Output captured mid-run can start anywhere.
        const string noisy = "Input #0, mov,mp4\n" +
                             "ffmpeg version 8.0.1 Copyright (c) 2000-2025 the FFmpeg developers\n";

        Assert.Equal("8.0.1", FfmpegVersion.BuildOf(noisy));
    }

    [Fact]
    public void HandlesCarriageReturns()
    {
        const string crlf =
            "ffmpeg version 8.0.1 Copyright (c) 2000-2025 the FFmpeg developers\r\n  built with gcc\r\n";

        Assert.Equal("8.0.1", FfmpegVersion.BuildOf(crlf));
    }

    [Fact]
    public void ReadsABannerWithNoCopyrightAfterIt()
    {
        Assert.Equal("9.9-custom", FfmpegVersion.BuildOf("ffmpeg version 9.9-custom\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Encoders:\n V..... = Video\n")]
    [InlineData("some other tool version 1.0\n")]
    public void SaysNothingWhenThereIsNoBanner(string? output)
    {
        Assert.Null(FfmpegVersion.FromBanner(output));
        Assert.Null(FfmpegVersion.BuildOf(output));
    }

    [Fact]
    public void SaysNothingWhenTheVersionItselfIsMissing()
    {
        Assert.Null(FfmpegVersion.BuildOf("ffmpeg version  Copyright (c) 2000-2025\n"));
    }
}
