namespace GameGround.Tests;

internal sealed class TemporaryDatasetFile : IDisposable
{
    public TemporaryDatasetFile(string json)
    {
        Path = System.IO.Path.GetTempFileName();
        File.WriteAllText(Path, json);
    }

    public string Path { get; }

    public void Dispose() => File.Delete(Path);
}
