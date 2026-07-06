using System.Diagnostics;
using ErrorOr;
using FluentValidation;
using MessageBridge.Publisher.Internal;
using MessageBridge.Publisher.Requests;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher;

internal sealed class DirectMessageBridgePublisher : IMessageBridgePublisher
{
    private const string DefaultW3CCorrelation = "00000000000000000000000000000000";

    private readonly MessageBridgePublisherOptions _options;
    private readonly IValidator<SendWhatsAppMessageRequest> _whatsAppValidator;
    private readonly IValidator<SendEmailConfirmationRequest> _emailValidator;
    private readonly IMessageBridgePublisherTransport _transport;

    public DirectMessageBridgePublisher(
        IOptions<MessageBridgePublisherOptions> options,
        IValidator<SendWhatsAppMessageRequest> whatsAppValidator,
        IValidator<SendEmailConfirmationRequest> emailValidator,
        IMessageBridgePublisherTransport transport)
    {
        _options = options.Value;
        _whatsAppValidator = whatsAppValidator;
        _emailValidator = emailValidator;
        _transport = transport;
    }

    public async Task<ErrorOr<MessageBridgePublisherResult>> PublishWhatsAppMessageAsync(
        SendWhatsAppMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Error.Validation("publisher.request", "Request cannot be null.");
        }

        var normalized = NormalizeWhatsApp(request);
        if (normalized.IsError)
        {
            return normalized.Errors;
        }

        var validation = await _whatsAppValidator.ValidateAsync(normalized.Value.Request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationErrors(validation);
        }

        var envelope = BuildEnvelope(
            _options.WhatsAppRoutingKey,
            "SendWhatsAppMessageCommand",
            "urn:message:MessageBridge.Contracts.V1:SendWhatsAppMessageCommand",
            MessageBridgePayloadSerializer.SerializeWhatsApp(normalized.Value.Request),
            normalized.Value);

        await _transport.PublishAsync(envelope, cancellationToken);
        return new MessageBridgePublisherResult(normalized.Value.MessageId, normalized.Value.CorrelationId, normalized.Value.TenantId);
    }

    public async Task<ErrorOr<MessageBridgePublisherResult>> PublishEmailConfirmationAsync(
        SendEmailConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Error.Validation("publisher.request", "Request cannot be null.");
        }

        var normalized = NormalizeEmail(request);
        if (normalized.IsError)
        {
            return normalized.Errors;
        }

        var validation = await _emailValidator.ValidateAsync(normalized.Value.Request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationErrors(validation);
        }

        var envelope = BuildEnvelope(
            _options.EmailRoutingKey,
            "SendEmailConfirmationCommand",
            "urn:message:MessageBridge.Contracts.V1:SendEmailConfirmationCommand",
            MessageBridgePayloadSerializer.SerializeEmail(normalized.Value.Request),
            normalized.Value.MessageId,
            normalized.Value.CorrelationId,
            normalized.Value.TenantId);

        await _transport.PublishAsync(envelope, cancellationToken);
        return new MessageBridgePublisherResult(normalized.Value.MessageId, normalized.Value.CorrelationId, normalized.Value.TenantId);
    }

    private static Error[] ValidationErrors(FluentValidation.Results.ValidationResult validation) =>
        [.. validation.Errors.Select(error => Error.Validation(error.PropertyName ?? "PublisherRequest", error.ErrorMessage))];

    private MessageBridgePublisherEnvelope BuildEnvelope(
        string routingKey,
        string commandName,
        string messageUrn,
        byte[] payload,
        NormalizedRequest<SendWhatsAppMessageRequest> normalized)
    {
        return BuildEnvelope(routingKey, commandName, messageUrn, payload, normalized.MessageId, normalized.CorrelationId, normalized.TenantId);
    }

    private MessageBridgePublisherEnvelope BuildEnvelope(
        string routingKey,
        string commandName,
        string messageUrn,
        byte[] payload,
        string messageId,
        string correlationId,
        string tenantId)
    {
        return new MessageBridgePublisherEnvelope(
            _options.ExchangeName,
            routingKey,
            messageId,
            correlationId,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-protobuf",
                ["MessageBridge-Command"] = commandName,
                ["MessageBridge-MessageUrn"] = messageUrn,
                ["x-command-type"] = commandName,
                ["x-tenant-id"] = tenantId,
                ["x-format"] = "protobuf",
            },
            payload);
    }

    private ErrorOr<NormalizedRequest<SendWhatsAppMessageRequest>> NormalizeWhatsApp(SendWhatsAppMessageRequest request)
    {
        var tenantResult = ResolveTenant(request.TenantId);
        if (tenantResult.IsError)
        {
            return tenantResult.Errors;
        }

        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? UlidGenerator.New()
            : request.MessageId;
        var correlationId = ResolveCorrelation(request.CorrelationId);

        return new NormalizedRequest<SendWhatsAppMessageRequest>(
            tenantResult.Value,
            messageId,
            correlationId,
            new SendWhatsAppMessageRequest
            {
                TenantId = tenantResult.Value,
                PhoneNumber = request.PhoneNumber,
                TemplateId = request.TemplateId,
                Body = request.Body,
                LanguageCode = request.LanguageCode,
                MessageId = messageId,
                CorrelationId = correlationId,
            });
    }

    private ErrorOr<NormalizedRequest<SendEmailConfirmationRequest>> NormalizeEmail(SendEmailConfirmationRequest request)
    {
        var tenantResult = ResolveTenant(request.TenantId);
        if (tenantResult.IsError)
        {
            return tenantResult.Errors;
        }

        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? UlidGenerator.New()
            : request.MessageId;
        var correlationId = ResolveCorrelation(request.CorrelationId);

        return new NormalizedRequest<SendEmailConfirmationRequest>(
            tenantResult.Value,
            messageId,
            correlationId,
            new SendEmailConfirmationRequest
            {
                TenantId = tenantResult.Value,
                Email = request.Email,
                ConfirmationCode = request.ConfirmationCode,
                MessageId = messageId,
                CorrelationId = correlationId,
            });
    }

    private ErrorOr<string> ResolveTenant(string? tenantId)
    {
        var effectiveTenant = string.IsNullOrWhiteSpace(tenantId)
            ? _options.DefaultTenantId
            : tenantId;

        if (string.IsNullOrWhiteSpace(effectiveTenant))
        {
            return Error.Validation("publisher.tenant.required", "TenantId is required.");
        }

        if (_options.AllowedTenantIds.Count > 0 &&
            !_options.AllowedTenantIds.Contains(effectiveTenant))
        {
            return Error.Validation("publisher.tenant.not_allowed", "TenantId is not allowed.");
        }

        return effectiveTenant;
    }

    private static string ResolveCorrelation(string? correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        var traceId = Activity.Current?.TraceId.ToString();
        return string.IsNullOrWhiteSpace(traceId) || traceId == DefaultW3CCorrelation
            ? UlidGenerator.New()
            : traceId;
    }

    private readonly record struct NormalizedRequest<T>(
        string TenantId,
        string MessageId,
        string CorrelationId,
        T Request);
}
