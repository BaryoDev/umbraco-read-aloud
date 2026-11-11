using BaryoDev.Umbraco.ReadAloud.Controllers;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>The body of the page voice options, with a fake Azure admin since no backoffice user is signed in.</summary>
public class PageVoiceOptionsTests
{
    private sealed class FakeAdmin(AzureVoiceStatusModel status) : IReadAloudAzureAdmin
    {
        public AzureStatusModel GetStatus() => throw new NotSupportedException();
        public Task<AzureTestModel> TestAsync(string? region, string? key, CancellationToken ct) => throw new NotSupportedException();
        public Task<AzureVoiceStatusModel> GetVoicesAsync(CancellationToken ct) => Task.FromResult(status);
        public AzureSaveModel Save(string? region, string? key, string savedBy) => throw new NotSupportedException();
        public AzureSaveModel Remove(string removedBy) => throw new NotSupportedException();
    }

    private sealed class Fixed(ReadAloudOptions value) : IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = value;
        public ReadAloudOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }

    private static async Task<PageVoiceOptionsModel> CallAsync(AzureVoiceStatusModel? status)
    {
        var options = new ReadAloudOptions { AllowedVoices = ["en-PH-RosaNeural", "en-GB-RyanNeural"] };
        IReadAloudAzureAdmin[] admins = status is null ? [] : [new FakeAdmin(status)];
        var controller = new ReadAloudVoicesController(new Fixed(options), admins);

        var result = await controller.Voices(default);

        return result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<PageVoiceOptionsModel>();
    }

    [Fact]
    public async Task A_voice_the_region_lacks_is_dropped_and_flagged()
    {
        var model = await CallAsync(new AzureVoiceStatusModel(true, 2, "southeastasia",
            [new AzureConfiguredVoice("en-PH-RosaNeural", true), new AzureConfiguredVoice("en-GB-RyanNeural", false)]));

        model.Voices.ShouldBe(["en-PH-RosaNeural"]);
        model.NotInRegion.ShouldBe(["en-GB-RyanNeural"]);
        model.FilteredByRegion.ShouldBeTrue();
    }

    [Fact]
    public async Task Every_allowed_voice_is_listed_when_no_region_list_is_known()
    {
        var model = await CallAsync(null);

        model.Voices.ShouldBe(["en-PH-RosaNeural", "en-GB-RyanNeural"]);
        model.NotInRegion.ShouldBeEmpty();
        model.FilteredByRegion.ShouldBeFalse();
    }
}
