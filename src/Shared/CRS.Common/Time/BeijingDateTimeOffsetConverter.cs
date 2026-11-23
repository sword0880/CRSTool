using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Timing;

/// <summary>在通信/持久化边界采用 +08:00，旧的显式偏移时间保持原时间点读入。</summary>
public sealed class BeijingDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => BeijingClock.Normalize(reader.GetDateTimeOffset());
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(BeijingClock.Normalize(value));
}
