using Qisland.Core;
using Xunit;

namespace Qisland.Core.Tests;

public class ClipPinsTests
{
    [Fact]
    public void Pin_MovesToFront_Dedupes_AndCaps()
    {
        var p = new ClipPins();
        for (var i = 0; i < 12; i++) Assert.True(p.Pin("t" + i));
        Assert.Equal(ClipPins.Max, p.Count);
        Assert.Equal("t11", p.Items[0]);
        Assert.DoesNotContain("t0", p.Items);
        p.Pin("t5");
        Assert.Equal("t5", p.Items[0]);
        Assert.Equal(ClipPins.Max, p.Count);
    }

    [Fact]
    public void RejectsBlankAndHugeText()
    {
        var p = new ClipPins();
        Assert.False(p.Pin("  "));
        Assert.False(p.Pin(new string('x', ClipPins.MaxLength + 1)));
        Assert.Equal(0, p.Count);
    }

    [Fact]
    public void RoundTrip_AndBadJsonIsEmpty()
    {
        var p = new ClipPins();
        p.Pin("a\nb"); p.Pin("“引号”");
        var q = ClipPins.Parse(p.Serialize());
        Assert.Equal(p.Items, q.Items);
        Assert.True(q.Unpin("a\nb"));
        Assert.Single(q.Items);
        Assert.Equal(0, ClipPins.Parse("{not json").Count);
        Assert.Equal(0, ClipPins.Parse(null).Count);
    }
}
