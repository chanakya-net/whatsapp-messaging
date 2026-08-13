using System.Text.Json;
using RabbitMQ.Client;

namespace MessageBridge.IntegrationTests.Fixtures;

internal sealed class RabbitMqEnvelopeProbe : IAsyncDisposable
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMilliseconds(750);
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queueName;
    private readonly Dictionary<string, List<ProbedMessageBridgeEnvelope>> _messages = [];

    private RabbitMqEnvelopeProbe(IConnection connection, IChannel channel, string queueName)
    {
        _connection = connection;
        _channel = channel;
        _queueName = queueName;
    }

    public static async Task<RabbitMqEnvelopeProbe> CreateAsync(
        string connectionString,
        string exchangeName)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(connectionString) }
            .CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var queueName = $"messagebridge-outbox-probe-{Guid.NewGuid():N}";

        try
        {
            await channel.ExchangeDeclareAsync(
                exchangeName,
                ExchangeType.Fanout,
                durable: false,
                autoDelete: true);
            await channel.QueueDeclareAsync(
                queueName,
                durable: false,
                exclusive: true,
                autoDelete: true);
            await channel.QueueBindAsync(queueName, exchangeName, string.Empty);
            return new RabbitMqEnvelopeProbe(connection, channel, queueName);
        }
        catch
        {
            await channel.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task AssertNoMessagesAsync(string messageId)
    {
        await IntegrationEnvironmentFixture.AssertRemainsAsync(
            async () =>
            {
                await DrainAsync();
                return Count(messageId) == 0;
            },
            ObservationWindow,
            $"Message '{messageId}' reached RabbitMQ before publication was allowed.");
    }

    public Task<ProbedMessageBridgeEnvelope> WaitForMessageAsync(string messageId) =>
        IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () =>
            {
                await DrainAsync();
                return _messages.TryGetValue(messageId, out var messages)
                    ? messages.FirstOrDefault()
                    : null;
            },
            $"Message '{messageId}' did not reach the RabbitMQ probe.");

    public async Task AssertCountRemainsAsync(string messageId, int expectedCount)
    {
        await IntegrationEnvironmentFixture.AssertRemainsAsync(
            async () =>
            {
                await DrainAsync();
                return Count(messageId) == expectedCount;
            },
            ObservationWindow,
            $"Message '{messageId}' was dispatched more than {expectedCount} time(s).");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_channel.IsOpen)
            {
                await _channel.QueueDeleteAsync(_queueName);
            }
        }
        finally
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private int Count(string messageId) =>
        _messages.TryGetValue(messageId, out var messages) ? messages.Count : 0;

    private async Task DrainAsync()
    {
        BasicGetResult? delivery;
        while ((delivery = await _channel.BasicGetAsync(_queueName, autoAck: true)) is not null)
        {
            var envelope = Deserialize(delivery.Body);
            if (!_messages.TryGetValue(envelope.MessageId, out var messages))
            {
                messages = [];
                _messages.Add(envelope.MessageId, messages);
            }

            messages.Add(envelope);
        }
    }

    private static ProbedMessageBridgeEnvelope Deserialize(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("message");
        return new ProbedMessageBridgeEnvelope(
            message.GetProperty("messageId").GetString()!,
            message.GetProperty("exchangeName").GetString()!,
            message.GetProperty("routingKey").GetString()!);
    }
}

internal sealed record ProbedMessageBridgeEnvelope(
    string MessageId,
    string ExchangeName,
    string RoutingKey);
