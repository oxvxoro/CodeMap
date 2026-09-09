using System.Text.Json;

namespace CodeMap.Cli.Presentation;

public interface ICliResultWriter
{
    void Write<T>(T value);

    void WriteError(int version, string code, string message);
}

public sealed class CliJsonWriter(TextWriter output, JsonSerializerOptions? options = null) : ICliResultWriter
{
    private readonly JsonSerializerOptions _options = options ?? new(JsonSerializerDefaults.Web);

    public void Write<T>(T value) => output.WriteLine(JsonSerializer.Serialize(value, _options));

    public void WriteError(int version, string code, string message) =>
        Write(new { version, error = new { code, message } });
}
