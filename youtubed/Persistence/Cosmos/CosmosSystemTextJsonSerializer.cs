using System;
using System.IO;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosSystemTextJsonSerializer : CosmosSerializer
    {
        internal static readonly CosmosSystemTextJsonSerializer Instance = new();

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private CosmosSystemTextJsonSerializer()
        {
        }

        public override T FromStream<T>(Stream stream)
        {
            if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                return (T)(object)stream;
            }

            using (stream)
            {
                return JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            }
        }

        public override Stream ToStream<T>(T input)
        {
            var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, input, SerializerOptions);
            stream.Position = 0;
            return stream;
        }

        internal int GetSerializedUtf8Size<T>(T input)
        {
            using var stream = ToStream(input);
            return checked((int)stream.Length);
        }

        internal string SerializeToString<T>(T input)
        {
            using var stream = ToStream(input);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
