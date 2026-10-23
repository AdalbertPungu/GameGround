using System.Text;
using GameGround.Core;

namespace GameGround.Data;

public sealed class DatasetLoader
{
    public MatchDataset Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        var bytes = File.ReadAllBytes(path);
        try
        {
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length
                : 0;
            json = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException error)
        {
            throw new DatasetJsonException("The dataset file must contain valid UTF-8.", null, error);
        }

        var dataset = new DatasetJson().Deserialize(json);
        DatasetValidator.Validate(dataset);
        return dataset;
    }
}
