using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Microsoft.Azure.Cosmos;

namespace Enfolderer.Ai.Infrastructure.Storage;

/// <summary>
/// Cosmos serializer backed by <see cref="ScanJson.Options"/>. The default serializer is
/// Newtonsoft-based and ignores the System.Text.Json attributes on the contract types, which would
/// persist <see cref="ScanJobStatus"/> as an integer and break the documented wire format.
/// </summary>
public sealed class SystemTextJsonCosmosSerializer : CosmosSerializer
{
    private static readonly JsonSerializerOptions Options = ScanJson.Options;

    public override T FromStream<T>(Stream stream)
    {
        // Cosmos asks for the raw stream back when the caller wants it verbatim; handing back a
        // disposed stream would break those callers.
        if (typeof(Stream).IsAssignableFrom(typeof(T))) return (T)(object)stream;

        using (stream)
        {
            return JsonSerializer.Deserialize<T>(stream, Options)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true }))
        {
            JsonSerializer.Serialize(writer, input, Options);
        }

        stream.Position = 0;
        return stream;
    }
}
