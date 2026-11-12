using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class MaxCharsValidationTests
{
    private static ValidateOptionsResult Validate(int maxChars) =>
        new ReadAloudOptionsValidation([], NullLogger<ReadAloudOptionsValidation>.Instance)
            .Validate(null, new ReadAloudOptions { MaxChars = maxChars });

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void A_max_chars_below_one_fails_and_names_the_setting(int value)
    {
        var result = Validate(value);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxChars");
    }

    [Fact]
    public void The_default_max_chars_passes()
    {
        Validate(8000).Failed.ShouldBeFalse();
    }
}

public class RateLimitPartitionTests
{
    [Fact]
    public void Two_addresses_in_one_ipv6_64_share_a_partition()
    {
        ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"))
            .ShouldBe(ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("2001:db8:1:2:bbbb::ffff")));
    }

    [Fact]
    public void Addresses_in_different_64s_do_not_share_a_partition()
    {
        ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("2001:db8:1:2::1"))
            .ShouldNotBe(ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void An_ipv4_mapped_address_is_the_ipv4_address()
    {
        ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("::ffff:203.0.113.9"))
            .ShouldBe(ReadAloudRateLimiterPolicy.PartitionKey(IPAddress.Parse("203.0.113.9")));
    }

    [Fact]
    public void No_address_still_gets_a_partition()
    {
        ReadAloudRateLimiterPolicy.PartitionKey(null).ShouldBe("unknown");
    }
}
