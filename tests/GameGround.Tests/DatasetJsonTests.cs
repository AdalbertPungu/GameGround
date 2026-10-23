using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using GameGround.Core;
using GameGround.Data;
using GameGround.Tests.Fixtures;

namespace GameGround.Tests;

public sealed class DatasetJsonTests
{
    private static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "Data", "match-001.json");

    [Fact]
    public void Reference_file_matches_every_business_field_of_the_independent_fixture()
    {
        var actual = new DatasetLoader().Load(ReferencePath);

        AssertBusinessEqual(SyntheticMatch.Create(), actual);
        Assert.Equal(new MatchSnapshot(
            "harbor-meadow-miniature", 20, 2, TimeSpan.FromMinutes(1), PeriodStatus.Ended,
            1, 1, BallControl.DeadBall, true), MatchReducer.Replay(actual));
    }

    [Fact]
    public void Every_json_prefix_matches_the_independent_fixture_snapshot()
    {
        var actual = new DatasetLoader().Load(ReferencePath);
        var expected = SyntheticMatch.Create();
        for (var length = 0; length <= expected.Events.Length; length++)
        {
            Assert.Equal(
                MatchReducer.Replay(expected with { Events = expected.Events.Take(length).ToImmutableArray() }),
                MatchReducer.Replay(actual with { Events = actual.Events.Take(length).ToImmutableArray() }));
        }
    }

    [Fact]
    public void Two_loads_produce_identical_events_and_snapshots()
    {
        var loader = new DatasetLoader();
        var first = loader.Load(ReferencePath);
        var second = loader.Load(ReferencePath);

        AssertBusinessEqual(first, second);
        Assert.Equal(MatchReducer.Replay(first), MatchReducer.Replay(second));
    }

    [Fact]
    public void Object_json_object_preserves_all_business_fields()
    {
        var codec = new DatasetJson();
        var original = SyntheticMatch.Create();
        var json = codec.Serialize(original);
        var restored = codec.Deserialize(json);

        DatasetValidator.Validate(restored);
        AssertBusinessEqual(original, restored);
        Assert.Equal(MatchReducer.Replay(original), MatchReducer.Replay(restored));
        Assert.Equal(json, codec.Serialize(restored));
    }

    [Fact]
    public void Serialization_uses_numeric_ids_and_explicit_synthetic_provenance()
    {
        var root = JsonNode.Parse(new DatasetJson().Serialize(SyntheticMatch.Create()))!;

        Assert.True(root["manifest"]!["isSynthetic"]!.GetValue<bool>());
        Assert.Equal(1, root["match"]!["homeTeam"]!["id"]!.GetValue<int>());
        Assert.Equal("pass", root["events"]![2]!["type"]!.GetValue<string>());
        Assert.Equal("completed", root["events"]![2]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void Parser_does_not_perform_business_validation()
    {
        var json = Mutate(root => root["manifest"]!["isSynthetic"] = false);
        var parsed = new DatasetJson().Deserialize(json);

        Assert.False(parsed.Manifest.IsSynthetic);
        var error = Assert.Throws<DomainValidationException>(() => DatasetValidator.Validate(parsed));
        Assert.Equal(ValidationError.InvalidManifest, error.Code);
    }

    [Theory]
    [InlineData("falseProvenance", ValidationError.InvalidManifest)]
    [InlineData("schemaVersion", ValidationError.InvalidManifest)]
    [InlineData("emptyDatasetId", ValidationError.InvalidManifest)]
    [InlineData("unknownTeam", ValidationError.UnknownTeam)]
    [InlineData("unknownPlayer", ValidationError.UnknownPlayer)]
    [InlineData("unknownRecipient", ValidationError.UnknownPlayer)]
    [InlineData("wrongPlayerTeam", ValidationError.PlayerTeamMismatch)]
    [InlineData("sequence", ValidationError.InvalidSequence)]
    [InlineData("duplicateEvent", ValidationError.DuplicateEvent)]
    [InlineData("duplicateGoal", ValidationError.DuplicateEvent)]
    [InlineData("negativeClock", ValidationError.InvalidClock)]
    [InlineData("lateClock", ValidationError.InvalidClock)]
    [InlineData("backwardsClock", ValidationError.InvalidClock)]
    [InlineData("period", ValidationError.InvalidPeriod)]
    [InlineData("wrongControlSource", ValidationError.InvalidControl)]
    [InlineData("unknownControlTeam", ValidationError.InvalidControl)]
    [InlineData("selfPass", ValidationError.InvalidAction)]
    [InlineData("duplicatePlayer", ValidationError.InvalidDefinition)]
    [InlineData("nullEvent", ValidationError.InvalidEvent)]
    [InlineData("nullPlayer", ValidationError.InvalidDefinition)]
    [InlineData("nullPeriod", ValidationError.InvalidDefinition)]
    public void Loader_preserves_explicit_domain_errors(string scenario, ValidationError expected)
    {
        var json = Mutate(root =>
        {
            var manifest = root["manifest"]!;
            var match = root["match"]!;
            var events = root["events"]!.AsArray();
            switch (scenario)
            {
                case "falseProvenance": manifest["isSynthetic"] = false; break;
                case "schemaVersion": manifest["schemaVersion"] = 2; break;
                case "emptyDatasetId": manifest["datasetId"] = ""; break;
                case "unknownTeam": events[2]!["team"] = 99; break;
                case "unknownPlayer": events[2]!["player"] = 99; break;
                case "unknownRecipient": events[2]!["recipient"] = 99; break;
                case "wrongPlayerTeam": events[2]!["player"] = 21; break;
                case "sequence": events[2]!["sequence"] = 9; break;
                case "duplicateEvent": events[2]!["id"] = 2; break;
                case "duplicateGoal": events[18]!["id"] = 9; break;
                case "negativeClock": events[2]!["clock"] = "-00:00:01"; break;
                case "lateClock": events[2]!["clock"] = "00:01:01"; break;
                case "backwardsClock": events[3]!["clock"] = "00:00:04"; break;
                case "period": events[2]!["period"] = 3; break;
                case "wrongControlSource": events[5]!["from"]!["team"] = 2; break;
                case "unknownControlTeam":
                    events[5]!["to"]!["kind"] = "team";
                    events[5]!["to"]!["team"] = 99; break;
                case "selfPass": events[2]!["recipient"] = 11; break;
                case "duplicatePlayer": match["awayTeam"]!["players"]![0]!["id"] = 11; break;
                case "nullEvent": events[2] = null; break;
                case "nullPlayer": match["homeTeam"]!["players"]![0] = null; break;
                case "nullPeriod": match["periods"]![0] = null; break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        });
        using var file = new TemporaryDatasetFile(json);

        var first = Assert.Throws<DomainValidationException>(() => new DatasetLoader().Load(file.Path));
        var second = Assert.Throws<DomainValidationException>(() => new DatasetLoader().Load(file.Path));

        Assert.Equal(expected, first.Code);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(first.EventId, second.EventId);
    }

    [Theory]
    [InlineData("missingManifest")]
    [InlineData("nullManifest")]
    [InlineData("missingProvenance")]
    [InlineData("missingVersion")]
    [InlineData("missingMatch")]
    [InlineData("nullMatch")]
    [InlineData("missingEvents")]
    [InlineData("nullEvents")]
    [InlineData("missingEventType")]
    [InlineData("unknownEventType")]
    [InlineData("unknownProperty")]
    [InlineData("wrongCaseProperty")]
    [InlineData("numericEnum")]
    [InlineData("numericStringEnum")]
    [InlineData("unknownOutcome")]
    [InlineData("missingOutcome")]
    [InlineData("missingSequence")]
    [InlineData("stringId")]
    [InlineData("fractionalId")]
    [InlineData("overflowId")]
    [InlineData("nullId")]
    [InlineData("missingControlTeam")]
    [InlineData("nullControl")]
    [InlineData("unknownControlKind")]
    [InlineData("invalidClockFormat")]
    [InlineData("eventTypeWrongShape")]
    [InlineData("nullPlayerName")]
    [InlineData("irrelevantEventField")]
    public void Structural_errors_are_distinct_from_domain_errors(string scenario)
    {
        var json = Mutate(root =>
        {
            var events = root["events"]!.AsArray();
            switch (scenario)
            {
                case "missingManifest": root.Remove("manifest"); break;
                case "nullManifest": root["manifest"] = null; break;
                case "missingProvenance": root["manifest"]!.AsObject().Remove("isSynthetic"); break;
                case "missingVersion": root["manifest"]!.AsObject().Remove("schemaVersion"); break;
                case "missingMatch": root.Remove("match"); break;
                case "nullMatch": root["match"] = null; break;
                case "missingEvents": root.Remove("events"); break;
                case "nullEvents": root["events"] = null; break;
                case "missingEventType": events[2]!.AsObject().Remove("type"); break;
                case "unknownEventType": events[2]!["type"] = "teleport"; break;
                case "unknownProperty": root["realPremierLeagueData"] = true; break;
                case "wrongCaseProperty":
                    root["Manifest"] = root["manifest"]!.DeepClone();
                    root.Remove("manifest"); break;
                case "numericEnum": events[2]!["outcome"] = 0; break;
                case "numericStringEnum": events[2]!["outcome"] = "0"; break;
                case "unknownOutcome": events[2]!["outcome"] = "teleported"; break;
                case "missingOutcome": events[2]!.AsObject().Remove("outcome"); break;
                case "missingSequence": events[2]!.AsObject().Remove("sequence"); break;
                case "stringId": events[2]!["id"] = "3"; break;
                case "fractionalId": events[2]!["id"] = 3.5; break;
                case "overflowId": events[2]!["id"] = long.MaxValue; break;
                case "nullId": events[2]!["id"] = null; break;
                case "missingControlTeam": events[1]!["from"]!.AsObject().Remove("team"); break;
                case "nullControl": events[1]!["from"] = null; break;
                case "unknownControlKind": events[1]!["from"]!["kind"] = "flying"; break;
                case "invalidClockFormat": events[2]!["clock"] = "five seconds"; break;
                case "eventTypeWrongShape": events[0]!["type"] = "shot"; break;
                case "nullPlayerName": root["match"]!["homeTeam"]!["players"]![0]!["name"] = null; break;
                case "irrelevantEventField": events[0]!["outcome"] = "goal"; break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        });
        using var file = new TemporaryDatasetFile(json);
        var first = Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));
        var second = Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));

        Assert.Equal("InvalidJson", first.Code);
        Assert.False(string.IsNullOrWhiteSpace(first.Message));
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(first.JsonPath, second.JsonPath);
        Assert.NotNull(first.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{} {}")]
    [InlineData("{\"manifest\": 42}")]
    public void Malformed_or_wrong_root_json_is_rejected(string json)
    {
        using var file = new TemporaryDatasetFile(json);
        Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 2")]
    [InlineData("\"team\": 1, \"player\": 11", "\"team\": 1, \"team\": 2, \"player\": 11")]
    [InlineData("\"type\": \"periodStarted\"", "\"type\": \"periodStarted\", \"type\": \"shot\"")]
    public void Duplicate_properties_are_rejected(string original, string duplicate)
    {
        var json = File.ReadAllText(ReferencePath).Replace(original, duplicate, StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(ReferencePath), json);
        using var file = new TemporaryDatasetFile(json);

        Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));
    }

    [Fact]
    public void Event_discriminator_can_appear_after_other_properties()
    {
        var root = JsonNode.Parse(File.ReadAllText(ReferencePath))!;
        var first = root["events"]![0]!.AsObject();
        first.Remove("type");
        first.Add("type", "periodStarted");
        using var file = new TemporaryDatasetFile(root.ToJsonString());

        AssertBusinessEqual(SyntheticMatch.Create(), new DatasetLoader().Load(file.Path));
    }

    [Fact]
    public void Empty_event_array_is_a_valid_not_started_replay()
    {
        using var file = new TemporaryDatasetFile(Mutate(root => root["events"] = new JsonArray()));
        var dataset = new DatasetLoader().Load(file.Path);

        Assert.Empty(dataset.Events);
        Assert.Equal(PeriodStatus.NotStarted, MatchReducer.Replay(dataset).PeriodStatus);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Serialization_and_parsing_are_culture_independent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var codec = new DatasetJson();
            var original = SyntheticMatch.Create();
            var pass = Assert.IsType<Pass>(original.Events[2]);
            original = original with
            {
                Events = original.Events.SetItem(2, pass with
                {
                    Clock = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 5 + 1234567)
                })
            };
            var restored = codec.Deserialize(codec.Serialize(original));
            AssertBusinessEqual(original, restored);
            Assert.Equal(MatchReducer.Replay(original), MatchReducer.Replay(restored));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void File_read_errors_are_not_reported_as_json_or_domain_errors()
    {
        using var file = new TemporaryDatasetFile("{}");
        File.Delete(file.Path);

        Assert.Throws<FileNotFoundException>(() => new DatasetLoader().Load(file.Path));
    }

    [Fact]
    public void Invalid_utf8_is_rejected_explicitly()
    {
        using var file = new TemporaryDatasetFile("{}");
        File.WriteAllBytes(file.Path, [0xFF, 0xFF, 0xFF]);

        Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));
    }

    [Fact]
    public void Utf16_bom_does_not_silently_change_the_encoding_contract()
    {
        using var file = new TemporaryDatasetFile("{}");
        File.WriteAllText(file.Path, File.ReadAllText(ReferencePath), System.Text.Encoding.Unicode);

        Assert.Throws<DatasetJsonException>(() => new DatasetLoader().Load(file.Path));
    }

    [Fact]
    public void Utf8_bom_is_supported()
    {
        using var file = new TemporaryDatasetFile("{}");
        File.WriteAllText(file.Path, File.ReadAllText(ReferencePath),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        AssertBusinessEqual(SyntheticMatch.Create(), new DatasetLoader().Load(file.Path));
    }

    private static string Mutate(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(ReferencePath))!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static void AssertBusinessEqual(MatchDataset expected, MatchDataset actual)
    {
        Assert.Equal(expected.Manifest, actual.Manifest);
        Assert.Equal(expected.Match.MatchId, actual.Match.MatchId);
        AssertTeamEqual(expected.Match.HomeTeam, actual.Match.HomeTeam);
        AssertTeamEqual(expected.Match.AwayTeam, actual.Match.AwayTeam);
        Assert.Equal(expected.Match.Periods.ToArray(), actual.Match.Periods.ToArray());
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
    }

    private static void AssertTeamEqual(Team expected, Team actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Players.ToArray(), actual.Players.ToArray());
    }
}
