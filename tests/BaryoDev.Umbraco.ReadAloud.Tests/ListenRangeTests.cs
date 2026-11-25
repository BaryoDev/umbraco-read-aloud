using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class ListenRangeTests
{
    [Fact]
    public void No_range_header_counts()
    {
        ListenRange.CountsAsPlay(StringValues.Empty).ShouldBeTrue();
    }

    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("BYTES=0-")]
    [InlineData(" bytes=0- ")]
    public void The_open_range_from_the_start_counts(string header)
    {
        ListenRange.CountsAsPlay(header).ShouldBeTrue();
    }

    [Theory]
    [InlineData("bytes=0-1")]
    [InlineData("bytes=500-")]
    [InlineData("bytes=-500")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=0-,5-6")]
    [InlineData("bytes=junk")]
    [InlineData("junk")]
    [InlineData("items=0-")]
    public void Any_other_range_counts_nothing(string header)
    {
        ListenRange.CountsAsPlay(header).ShouldBeFalse();
    }

    [Fact]
    public void Two_range_headers_count_nothing()
    {
        ListenRange.CountsAsPlay(new StringValues(["bytes=0-", "bytes=0-"])).ShouldBeFalse();
    }
}
