using GameGround.Cli;
using GameGround.Data;
using GameGround.Tests.Fixtures;

namespace GameGround.Tests;

public sealed class DatasetCommandTests
{
    [Theory]
    [InlineData("validate")]
    [InlineData("replay")]
    public void Valid_command_reports_dataset_count_and_score(string command)
    {
        using var file = ValidFile();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = DatasetCommand.Run([command, file.Path], output, error);

        Assert.Equal(0, exit);
        Assert.Contains("Dataset: synthetic-miniature-v1", output.ToString());
        Assert.Contains("Validation: valid (synthetic)", output.ToString());
        Assert.Contains("Events: 20", output.ToString());
        Assert.Contains("Score: Harbor FC 1-1 Meadow United", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        if (command == "replay")
        {
            Assert.Contains("sequence=20; period=2; clock=00:01:00", output.ToString());
            Assert.Contains("complete=True", output.ToString());
        }
        else
        {
            Assert.DoesNotContain("Snapshot:", output.ToString());
        }
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("replay")]
    public void Malformed_json_returns_failure_and_no_success_output(string command)
    {
        using var file = new TemporaryDatasetFile("{");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, DatasetCommand.Run([command, file.Path], output, error));
        Assert.Contains("[InvalidJson]", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Domain_failure_preserves_the_business_error_code()
    {
        var dataset = SyntheticMatch.Create();
        using var file = new TemporaryDatasetFile(new DatasetJson().Serialize(dataset with
        {
            Manifest = dataset.Manifest with { IsSynthetic = false }
        }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, DatasetCommand.Run(["validate", file.Path], output, error));
        Assert.Contains("[InvalidManifest]", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Event_failure_includes_event_identity()
    {
        var dataset = SyntheticMatch.Create();
        using var file = new TemporaryDatasetFile(new DatasetJson().Serialize(dataset with
        {
            Events = dataset.Events.SetItem(2, dataset.Events[2] with { Sequence = 99 })
        }));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, DatasetCommand.Run(["replay", file.Path], output, error));
        Assert.Contains("[InvalidSequence] event=3", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Missing_file_returns_a_file_error()
    {
        using var file = ValidFile();
        File.Delete(file.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, DatasetCommand.Run(["validate", file.Path], output, error));
        Assert.Contains("[FileReadError]", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("inspect")]
    [InlineData("VALIDATE")]
    public void Unknown_command_returns_usage_error(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, DatasetCommand.Run([command, "unused.json"], output, error));
        Assert.Contains("Usage:", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Missing_arguments_return_usage_error()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, DatasetCommand.Run([], output, error));
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public void Empty_path_returns_usage_error()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, DatasetCommand.Run(["validate", " "], output, error));
        Assert.Contains("Usage:", error.ToString());
    }

    private static TemporaryDatasetFile ValidFile() =>
        new(new DatasetJson().Serialize(SyntheticMatch.Create()));
}
