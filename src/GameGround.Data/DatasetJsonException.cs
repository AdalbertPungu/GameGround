namespace GameGround.Data;

public sealed class DatasetJsonException(string message, string? jsonPath, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code => "InvalidJson";
    public string? JsonPath { get; } = jsonPath;
}
