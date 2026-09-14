using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using FCG.IntegrationEvents.V1;

namespace FCG.Notifications.Function;

public sealed class NotificationHandler(
    INotificationStore store,
    string userQueueArn,
    string paymentQueueArn,
    Action<string> log)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SQSBatchResponse> HandleAsync(SQSEvent input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Records is null || input.Records.Any(r => r is null || string.IsNullOrWhiteSpace(r.MessageId)))
            throw new ArgumentException("SQS records must have message identifiers.");

        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var message in input.Records)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var notification = Parse(message);
                var created = await store.RecordSimulationAsync(notification, cancellationToken);
                log(JsonSerializer.Serialize(new
                {
                    level = "Information", messageId = message.MessageId,
                    eventKey = notification.EventKey, eventType = notification.EventType,
                    notificationKind = notification.NotificationKind,
                    result = created ? "SimulationRecorded" : "Duplicate",
                    processingStatus = "Completed"
                }));
            }
            catch (Exception error)
            {
                // Do not log exception messages, bodies, email addresses or credential values.
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
                log(JsonSerializer.Serialize(new
                {
                    level = "Error", messageId = message.MessageId,
                    result = "Retry", errorType = error.GetType().Name
                }));
            }
        }
        return new SQSBatchResponse(failures);
    }

    private NotificationRecord Parse(SQSEvent.SQSMessage message)
    {
        if (message.EventSourceArn == userQueueArn)
        {
            var value = JsonSerializer.Deserialize<UserCreatedEvent>(message.Body, JsonOptions)
                ?? throw new ArgumentException("Missing user event.");
            if (value.EventId == Guid.Empty || value.UserId == Guid.Empty ||
                value.OccurredAtUtc == default || string.IsNullOrWhiteSpace(value.Name) || !IsEmail(value.Email))
                throw new ArgumentException("Invalid user event.");
            return new NotificationRecord($"user-created:{value.EventId:D}", nameof(UserCreatedEvent),
                "Welcome", value.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                new Dictionary<string, string> { ["UserId"] = value.UserId.ToString("D") });
        }
        if (message.EventSourceArn == paymentQueueArn)
        {
            var value = JsonSerializer.Deserialize<PaymentProcessedEvent>(message.Body, JsonOptions)
                ?? throw new ArgumentException("Missing payment event.");
            var approved = string.Equals(value.Status, PaymentStatuses.Approved, StringComparison.OrdinalIgnoreCase);
            var rejected = string.Equals(value.Status, PaymentStatuses.Rejected, StringComparison.OrdinalIgnoreCase);
            if (value.OrderId == Guid.Empty || value.UserId == Guid.Empty || value.GameId == Guid.Empty ||
                value.ProcessedAtUtc == default || value.Price <= 0 || !IsEmail(value.UserEmail) || !(approved || rejected))
                throw new ArgumentException("Invalid payment event.");
            return new NotificationRecord($"payment-processed:{value.OrderId:D}", nameof(PaymentProcessedEvent),
                approved ? "PurchaseConfirmation" : "None",
                value.ProcessedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                new Dictionary<string, string>
                {
                    ["OrderId"] = value.OrderId.ToString("D"), ["UserId"] = value.UserId.ToString("D"),
                    ["GameId"] = value.GameId.ToString("D"),
                    ["Price"] = value.Price.ToString("G29", CultureInfo.InvariantCulture),
                    ["Status"] = approved ? PaymentStatuses.Approved : PaymentStatuses.Rejected
                });
        }
        throw new ArgumentException("Unrecognized source queue.");
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value, out var address) &&
        string.Equals(address.Address, value, StringComparison.Ordinal);
}
