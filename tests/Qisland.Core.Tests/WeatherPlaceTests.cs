using Qisland.Core;
using Xunit;

namespace Qisland.Core.Tests;

public class WeatherPlaceTests
{
    [Fact] public void EmptyOrCommentsOnlyMeansAutoByIp()
    {
        Assert.Equal(WeatherPlace.Auto, WeatherPlace.Parse(null));
        Assert.Equal(WeatherPlace.Auto, WeatherPlace.Parse("# 注释\n\n  # 还是注释\n"));
        Assert.Equal("https://wttr.in/?format=j1&lang=zh", WeatherPlace.Auto.WttrUrl);
        Assert.Null(WeatherPlace.Auto.Coordinates);
    }

    [Fact] public void SingleWordIsBothLabelAndQuery()
    {
        var p = WeatherPlace.Parse("# x\nShanghai\n");
        Assert.Equal(new WeatherPlace("Shanghai", "Shanghai"), p);
        Assert.Equal("https://wttr.in/Shanghai?format=j1&lang=zh", p.WttrUrl);
    }

    [Fact] public void LabelAndCoordinates()
    {
        var p = WeatherPlace.Parse("杭州  30.27,120.15");
        Assert.Equal("杭州", p.Display);
        Assert.Equal("https://wttr.in/30.27,120.15?format=j1&lang=zh", p.WttrUrl);
        Assert.Equal((30.27, 120.15), p.Coordinates);
        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=30.27&longitude=120.15&", WeatherPlace.OpenMeteoUrl(30.27, 120.15));
    }

    [Fact] public void LabelWithMultiWordQueryIsEscaped()
    {
        var p = WeatherPlace.Parse("纽约 New York");
        Assert.Equal("New York", p.Query);
        Assert.Equal("https://wttr.in/New%20York?format=j1&lang=zh", p.WttrUrl);
        Assert.Null(p.Coordinates);
    }

    [Fact] public void OutOfRangeCoordinatesAreNotCoordinates()
        => Assert.Null(WeatherPlace.Parse("x 200,10").Coordinates);
}
