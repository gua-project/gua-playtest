using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gua.Playtest.Core.Contracts;

internal static class BoundedContractJson
{
    // JsonNode/host evidence may already exist in memory. Serialization itself
    // must not first create an unbounded string or UTF-8 copy of that evidence.
    private sealed class Buffer : IBufferWriter<byte>
    {
        private byte[] bytes = new byte[4096];
        private int count;
        public void Advance(int amount)
        {
            if (amount < 0 || amount > bytes.Length - count) throw new ContractException("DocumentTooLarge");
            count += amount;
        }
        private void Check(int sizeHint)
        {
            if (sizeHint < 0 || Math.Max(1, sizeHint) > ContractDecoder.MaxBytes - count) throw new ContractException("DocumentTooLarge");
            var required = count + Math.Max(1, sizeHint);
            if (required > bytes.Length) Array.Resize(ref bytes, Math.Min(ContractDecoder.MaxBytes, Math.Max(required, bytes.Length * 2)));
        }
        public Memory<byte> GetMemory(int sizeHint = 0) { Check(sizeHint); return bytes.AsMemory(count); }
        public Span<byte> GetSpan(int sizeHint = 0) { Check(sizeHint); return bytes.AsSpan(count); }
        public byte[] Copy() => bytes.AsSpan(0, count).ToArray();
    }
    public static JsonObject Encode<T>(T value)
    {
        var buffer = new Buffer();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 64 }))
        { JsonSerializer.Serialize(writer, value, ContractJson.Options); writer.Flush(); }
        return ContractDecoder.Decode(buffer.Copy(), ".json");
    }
}
