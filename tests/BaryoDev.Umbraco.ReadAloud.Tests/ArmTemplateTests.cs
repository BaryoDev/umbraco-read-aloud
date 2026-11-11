using System.Text.Json;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>The Deploy to Azure template. It creates the Speech resource and must never hand back a key.</summary>
public class ArmTemplateTests
{
    private static string TemplatePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "azure", "speech-resource.json")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("azure/speech-resource.json was not found above the test output folder.");
        return Path.Combine(dir.FullName, "azure", "speech-resource.json");
    }

    private static JsonElement Template() => JsonDocument.Parse(File.ReadAllText(TemplatePath())).RootElement.Clone();

    [Fact]
    public void The_template_creates_one_speech_services_account()
    {
        var resource = Template().GetProperty("resources").EnumerateArray().Single();

        resource.GetProperty("type").GetString().ShouldBe("Microsoft.CognitiveServices/accounts");
        resource.GetProperty("kind").GetString().ShouldBe("SpeechServices");
    }

    [Fact]
    public void The_default_tier_is_the_free_one_and_s0_is_allowed()
    {
        var sku = Template().GetProperty("parameters").GetProperty("sku");

        sku.GetProperty("defaultValue").GetString().ShouldBe("F0");
        sku.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()).ShouldBe(["F0", "S0"]);
    }

    [Fact]
    public void Name_and_location_have_defaults()
    {
        var parameters = Template().GetProperty("parameters");

        parameters.GetProperty("name").TryGetProperty("defaultValue", out _).ShouldBeTrue();
        parameters.GetProperty("location").TryGetProperty("defaultValue", out _).ShouldBeTrue();
    }

    [Fact]
    public void The_template_has_no_outputs_because_deployment_outputs_stay_readable_in_history()
    {
        Template().TryGetProperty("outputs", out _).ShouldBeFalse();
        File.ReadAllText(TemplatePath()).ShouldNotContain("listKeys");
    }
}
