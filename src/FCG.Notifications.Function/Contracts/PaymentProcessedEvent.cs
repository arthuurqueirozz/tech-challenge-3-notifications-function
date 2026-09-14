namespace FCG.IntegrationEvents.V1;

public sealed record PaymentProcessedEvent(
    Guid OrderId,
    DateTime ProcessedAtUtc,
    Guid UserId,
    string UserEmail,
    Guid GameId,
    decimal Price,
    string Status);
