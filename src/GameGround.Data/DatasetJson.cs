using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameGround.Core;

namespace GameGround.Data;

public sealed class DatasetJson
{
    private readonly JsonSerializerOptions options;

    public DatasetJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(MatchEvent))
            {
                return;
            }

            typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "type",
                DerivedTypes =
                {
                    new(typeof(PeriodStarted), "periodStarted"),
                    new(typeof(PeriodEnded), "periodEnded"),
                    new(typeof(Pass), "pass"),
                    new(typeof(Shot), "shot"),
                    new(typeof(Tackle), "tackle"),
                    new(typeof(Foul), "foul"),
                    new(typeof(Pressure), "pressure"),
                    new(typeof(ControlChanged), "controlChanged")
                }
            };
        });

        options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = true,
            TypeInfoResolver = resolver
        };
        options.Converters.Add(new IdentifierConverter<TeamId>(value => new(value), id => id.Value));
        options.Converters.Add(new IdentifierConverter<PlayerId>(value => new(value), id => id.Value));
        options.Converters.Add(new IdentifierConverter<EventId>(value => new(value), id => id.Value));
        options.Converters.Add(new JsonStringEnumConverter<PassOutcome>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ShotOutcome>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<BallControlKind>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
    }

    public MatchDataset Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<MatchDataset>(json, options)
                ?? throw new JsonException("The dataset root must be an object, not null.");
        }
        catch (JsonException error)
        {
            throw new DatasetJsonException(error.Message, error.Path, error);
        }
        catch (NotSupportedException error)
        {
            throw new DatasetJsonException(
                "An event must specify a supported 'type' discriminator.", null, error);
        }
    }

    public string Serialize(MatchDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return JsonSerializer.Serialize(dataset, options);
    }

    private sealed class IdentifierConverter<TId>(Func<int, TId> create, Func<TId, int> value)
        : JsonConverter<TId> where TId : struct
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var id))
            {
                throw new JsonException("An identifier must be a 32-bit JSON integer.");
            }

            return create(id);
        }

        public override void Write(Utf8JsonWriter writer, TId id, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value(id));
    }
}
