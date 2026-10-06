using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gua.Playtest.Core.Assertions;
using Xunit;

namespace Gua.Playtest.Contracts.Tests;

public sealed class ConcurrentAssertionValidationTests
{
    [Fact]
    public async Task ConcurrentValidComparisonsNeverBecomeConfigurationOrObservationErrors()
    {
        // Independent fixed valid examples exercise shared Value and condition reference graphs.
        (string Actual, string Expected, string Operator)[] examples =
        [
            ("{\"type\":\"bool\",\"value\":true}", "{\"type\":\"bool\",\"value\":true}", "equals"),
            ("{\"type\":\"number\",\"value\":1.5}", "{\"type\":\"number\",\"value\":1.5}", "equals"),
            ("{\"type\":\"string\",\"value\":\"ok\"}", "{\"type\":\"string\",\"value\":\"ok\"}", "equals"),
            ("{\"type\":\"list\",\"elementType\":\"bool\",\"value\":[true,false]}", "{\"type\":\"integer\",\"value\":2}", "countEquals"),
            ("{\"type\":\"set\",\"elementType\":\"enum\",\"enumType\":\"game.Phase\",\"value\":[\"Second\",\"First\"]}", "{\"type\":\"set\",\"elementType\":\"enum\",\"enumType\":\"game.Phase\",\"value\":[\"First\",\"Second\"]}", "equals")
        ];
        const int workers = 8, rounds = 24;
        using var boundary = new Barrier(workers);
        var failures = new ConcurrentQueue<string>();
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            for (int round = 0; round < rounds; round++)
            {
                if (!boundary.SignalAndWait(TimeSpan.FromSeconds(20)))
                {
                    failures.Enqueue("Concurrent test boundary timed out.");
                    return;
                }
                int index = (worker + round) % examples.Length;
                try
                {
                    var catalog = EnumCatalogSnapshot.Create(JsonNode.Parse("{\"schemaVersion\":1,\"enums\":[{\"enumType\":\"game.Phase\",\"members\":[\"First\",\"Second\"]}]}")!.AsObject());
                    var sample = examples[index];
                    var type = JsonNode.Parse(sample.Actual)!.AsObject(); type.Remove("value");
                    var config = new JsonObject
                    {
                        ["kind"] = "assertion", ["operator"] = sample.Operator, ["quantifier"] = "one",
                        ["expected"] = JsonNode.Parse(sample.Expected),
                        ["read"] = new JsonObject
                        {
                            ["target"] = new JsonObject { ["source"] = "world" }, ["region"] = "property", ["name"] = "test", ["valueType"] = type
                        }
                    };
                    var result = PreparedAssertion.Create(config, new(10, 100), catalog).EvaluateJson(sample.Actual, catalog);
                    if (result != EvaluationResult.Known(true)) failures.Enqueue($"Case{index}: {result.Truth}/{result.Error}/{result.Code}");
                    var unexpected = JsonNode.Parse(sample.Actual)!.AsObject(); unexpected["unexpected"] = true;
                    var invalidObservation = PreparedAssertion.Create(config, new(10, 100), catalog).Evaluate(unexpected, catalog);
                    if (invalidObservation.Error != EvaluationError.ObservationContractViolation)
                        failures.Enqueue($"Case{index}: malformed observation accepted");
                    config["unexpected"] = true;
                    try
                    {
                        PreparedAssertion.Create(config, new(10, 100), catalog);
                        failures.Enqueue($"Case{index}: malformed configuration accepted");
                    }
                    catch (AssertionConfigurationException) { }
                }
                catch (AssertionConfigurationException ex) { failures.Enqueue($"Case{index}: {ex.Code}"); }
                catch (Exception ex) { failures.Enqueue($"Case{index}: {ex.GetType().Name}"); }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks);
        Assert.Empty(failures);
    }
}
