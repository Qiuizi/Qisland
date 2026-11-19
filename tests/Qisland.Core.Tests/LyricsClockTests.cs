using Qisland.Core;

public class LrcV4Tests
{
    [Fact]
    public void OffsetTagAnywhereAppliesToWholeSong()
    {
        var l = Lrc.Parse("[00:10.00]一\n[offset:+500]\n[00:20.00]二");
        Assert.Equal(TimeSpan.FromMilliseconds(9500), l[0].At);
        Assert.Equal(TimeSpan.FromMilliseconds(19500), l[1].At);
    }

    [Fact]
    public void NegativeOffsetDelays()
    {
        var l = Lrc.Parse("[offset:-250]\n[00:01.00]a");
        Assert.Equal(TimeSpan.FromMilliseconds(1250), l[0].At);
    }

    [Fact]
    public void SameTimestampBilingualLinesBecomeTranslation()
    {
        var l = Lrc.Parse("[00:05.00]Hello\n[00:05.00]你好\n[00:08.00]World\n[00:08.00]世界");
        Assert.Equal(2, l.Count);
        Assert.Equal("Hello", l[0].Text);
        Assert.Equal("你好", l[0].Translation);
        Assert.Equal("World", l[1].Text);
        Assert.Equal(1, Lrc.IndexAt(l, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void MultiTimestampLinesKeepOrderAndExplicitTranslationWins()
    {
        var l = Lrc.Parse("[00:01.00][00:20.00]副歌\n[00:10.00]主歌", "[00:01.00]chorus\n[00:10.05]verse\n[00:20.00]chorus");
        Assert.Equal(new[] { "副歌", "主歌", "副歌" }, l.Select(x => x.Text));
        Assert.Equal(new[] { "chorus", "verse", "chorus" }, l.Select(x => x.Translation));
    }

    [Fact]
    public void ThreeDigitAndColonFractions()
    {
        var l = Lrc.Parse("[01:02.345]a\n[01:03:50]b");
        Assert.Equal(new TimeSpan(0, 0, 1, 2, 345), l[0].At);
        Assert.Equal(new TimeSpan(0, 0, 1, 3, 500), l[1].At);
    }
}

public class LyricMatchTests
{
    static LyricCandidate C(string id, string t, string a, double d) => new(id, t, [a], d);

    [Fact]
    public void RejectsLiveVersionWhenStudioPlaying()
    {
        var best = LyricMatch.Best("晴天", "周杰伦", 269, [C("1", "晴天 (Live)", "周杰伦", 270), C("2", "晴天", "周杰伦", 269.5)]);
        Assert.Equal("2", best?.Id);
        Assert.Null(LyricMatch.Best("晴天", "周杰伦", 269, [C("1", "晴天 (Live)", "周杰伦", 269)]));
    }

    [Fact]
    public void RejectsDurationOverThreeSeconds()
    {
        Assert.Null(LyricMatch.Score("Song", "A", 200, C("1", "Song", "A", 204)));
        Assert.NotNull(LyricMatch.Score("Song", "A", 200, C("1", "Song", "A", 202.5)));
    }

    [Fact]
    public void UnknownDurationStillMatches() => Assert.NotNull(LyricMatch.Score("Song", "A", 0, C("1", "Song", "A", 210)));

    [Fact]
    public void PrefersClosestDuration()
    {
        var best = LyricMatch.Best("Song", "A", 200, [C("far", "Song", "A", 202.8), C("near", "Song", "A", 200.2)]);
        Assert.Equal("near", best?.Id);
    }

    [Fact]
    public void ArtistFeatAndDuet()
    {
        Assert.True(LyricMatch.ArtistOk("周杰伦 / 费玉清", ["周杰伦"], out _));
        Assert.True(LyricMatch.ArtistOk("Artist feat. Other", ["Artist"], out _));
        Assert.False(LyricMatch.ArtistOk("别人", ["周杰伦"], out _));
    }

    [Fact]
    public void WantedVariantPrefersVariant()
    {
        var best = LyricMatch.Best("晴天 (Live)", "周杰伦", 300, [C("studio", "晴天", "周杰伦", 300), C("live", "晴天 (Live)", "周杰伦", 300)]);
        Assert.Equal("live", best?.Id);
    }

    [Theory]
    [InlineData("Taylor Swift \u2014 1989", null, "Taylor Swift", "1989")]
    [InlineData("Taylor Swift", "1989", "Taylor Swift", "1989")]
    [InlineData("A \u2014 B", "Album", "A", "Album")]
    public void AppleMusicArtistSplit(string artist, string? album, string wantArtist, string? wantAlbum)
    {
        var (a, b) = MediaText.SplitAppleArtist(artist, album);
        Assert.Equal(wantArtist, a);
        Assert.Equal(wantAlbum, b);
    }

    [Fact]
    public void TitleArtistSplitOnlyWhenArtistMissing()
    {
        Assert.Equal(("晴天", "周杰伦"), MediaText.SplitTitleArtist("晴天 - 周杰伦", ""));
        Assert.Equal(("A - B", "C"), MediaText.SplitTitleArtist("A - B", "C"));
    }
}

public class PlaybackClockTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0);

    [Fact]
    public void ExtrapolatesBetweenSamples()
    {
        var c = new PlaybackClock();
        c.Sample("s", TimeSpan.FromSeconds(10), T0, true, T0);
        Assert.Equal(TimeSpan.FromSeconds(15), c.Position(T0.AddSeconds(5)));
    }

    [Fact]
    public void RepeatedStaleSampleDoesNotPullBack()
    {
        var c = new PlaybackClock();
        c.Sample("s", TimeSpan.Zero, T0, true, T0);
        // 播放器只在开始时报过一次，之后一直重复同一份（Apple Music 常见）
        Assert.False(c.Sample("s", TimeSpan.Zero, T0, true, T0.AddSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), c.Position(T0.AddSeconds(30)));
    }

    [Fact]
    public void SmallJitterIgnoredLargeDriftResyncs()
    {
        var c = new PlaybackClock();
        c.Sample("s", TimeSpan.FromSeconds(10), T0, true, T0);
        Assert.False(c.Sample("s", TimeSpan.FromSeconds(20.2), T0.AddSeconds(10), true, T0.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(20), c.Position(T0.AddSeconds(10)));
        Assert.True(c.Sample("s", TimeSpan.FromSeconds(60), T0.AddSeconds(11), true, T0.AddSeconds(11))); // 拖进度
        Assert.Equal(TimeSpan.FromSeconds(60), c.Position(T0.AddSeconds(11)));
        Assert.True(c.Live);
    }

    [Fact]
    public void PauseFreezesAndResumeContinues()
    {
        var c = new PlaybackClock();
        c.Sample("s", TimeSpan.FromSeconds(10), T0, true, T0);
        c.Sample("s", TimeSpan.FromSeconds(12), T0.AddSeconds(2), false, T0.AddSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(12), c.Position(T0.AddSeconds(100)));
        c.Sample("s", TimeSpan.FromSeconds(12), T0.AddSeconds(100), true, T0.AddSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(13), c.Position(T0.AddSeconds(101)));
    }

    [Fact]
    public void TrackChangeResetsAndBogusTimestampUsesNow()
    {
        var c = new PlaybackClock();
        c.Sample("a", TimeSpan.FromSeconds(100), T0, true, T0);
        Assert.True(c.Sample("b", TimeSpan.Zero, new DateTime(1601, 1, 1), true, T0.AddSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), c.Position(T0.AddSeconds(3)));
        Assert.False(c.Live);
    }

    [Fact]
    public void ClampsToDuration()
    {
        var c = new PlaybackClock();
        c.Sample("s", TimeSpan.FromSeconds(190), T0, true, T0);
        Assert.Equal(TimeSpan.FromSeconds(200), c.Position(T0.AddSeconds(30), TimeSpan.FromSeconds(200)));
    }
}

