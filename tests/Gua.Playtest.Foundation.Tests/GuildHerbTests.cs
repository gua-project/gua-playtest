using System.Text.Json;
using Gua.Playtest.Cli;
using Gua.Playtest.Core.Contracts;
using Xunit;

namespace Gua.Playtest.Foundation.Tests;

public class GuildHerbTests
{
    private static T Load<T>(string file) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../fixtures/guild-herb", file)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Theory]
    [InlineData("same", 1, true)]
    [InlineData("same", 2, false)]
    [InlineData("other", 1, false)]
    public void OnlyOriginalSequenceHostMayAuthorizeNextStep(string source, int epoch, bool same)
    {
        bool began = false;
        void Dispatch()
        {
            GuaObservationConnection.RequireSameSequenceHost(("same", 1UL), source, (ulong)epoch);
            began = true;
        }
        if (same) Dispatch();
        else Assert.Throws<InvalidOperationException>(Dispatch);
        Assert.Equal(same, began);
    }

    [Theory]
    [InlineData("player-sequence")]
    [InlineData("single-click")]
    [InlineData("debug")]
    [InlineData("missing-read")]
    [InlineData("raw-input")]
    public void GuildGoalAndPermissionsStayBoundToExplicitLane(string variant)
    {
        var scenario = Load<ScenarioDocument>("scenario.json");
        var environment = Load<EnvironmentDocument>("environment.json");
        if (variant == "debug") environment = environment with { Profile = "Debug" };
        if (variant == "missing-read") environment = environment with
            { Permissions = environment.Permissions with { Reads = environment.Permissions.Reads.Skip(1).ToArray() } };
        if (variant == "raw-input") environment = environment with
            { Permissions = environment.Permissions with { RawInput = true } };

        void Validate() => ObservationRunCommand.ValidateLane(scenario, environment, true, variant != "single-click");
        if (variant == "player-sequence") Validate();
        else Assert.Throws<InvalidDataException>(Validate);
    }
}
