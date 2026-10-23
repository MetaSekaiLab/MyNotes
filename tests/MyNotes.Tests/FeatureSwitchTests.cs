using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class FeatureSwitchTests
{
    [Fact]
    public void EmptyConfigurationReturnsEmptyMessage() =>
        Assert.Empty(new FeatureSwitchService(new ConfigurationBuilder().Build()).Get());

    [Fact]
    public void ConfigurationEncodesBooleanMap()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeatureSwitches:exampleEnabled"] = "true",
            ["FeatureSwitches:optionalEnabled"] = "false"
        }).Build();
        using var message = new CodedInputStream(new FeatureSwitchService(config).Get());
        var switches = new Dictionary<string, bool>();
        while (!message.IsAtEnd)
        {
            Assert.Equal(10u, message.ReadTag());
            using var entry = new CodedInputStream(message.ReadBytes().ToByteArray());
            Assert.Equal(10u, entry.ReadTag());
            var key = entry.ReadString();
            Assert.Equal(16u, entry.ReadTag());
            switches.Add(key, entry.ReadBool());
            Assert.True(entry.IsAtEnd);
        }
        Assert.Equal(2, switches.Count);
        Assert.True(switches["exampleEnabled"]);
        Assert.False(switches["optionalEnabled"]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidConfigurationIsRejected(string? value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FeatureSwitches:exampleEnabled"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => new FeatureSwitchService(config));
    }
}
