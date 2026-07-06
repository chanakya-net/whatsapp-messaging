using Microsoft.EntityFrameworkCore;

namespace MessageBridge.Publisher.EntityFrameworkCore.Outbox;

public sealed class MessageBridgeOutboxWriter : IMessageBridgeOutboxWriter
{
    private readonly DbContext _context;

    public MessageBridgeOutboxWriter(DbContext context)
    {
        _context = context;
    }

    public async Task WriteAsync(MessageBridgeOutboxMessage message, CancellationToken cancellationToken = default)
    {
        _context.Set<MessageBridgeOutboxMessage>().Add(message);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
