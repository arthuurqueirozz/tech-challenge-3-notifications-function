using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace FCG.Notifications.Function;

public sealed class DynamoNotificationStore(IAmazonDynamoDB client, string table) : INotificationStore
{
    public async Task<bool> RecordSimulationAsync(NotificationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                ConditionExpression = "attribute_not_exists(EventKey)",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["EventKey"] = new() { S = record.EventKey },
                    ["EventType"] = new() { S = record.EventType },
                    ["OccurredAtUtc"] = new() { S = record.OccurredAtUtc },
                    ["ProcessedAtUtc"] = new() { S = DateTime.UtcNow.ToString("O") },
                    ["ProcessingStatus"] = new() { S = "Completed" },
                    ["NotificationKind"] = new() { S = record.NotificationKind },
                    ["NotificationState"] = new() { S = record.NotificationKind == "None" ? "Skipped" : "Simulated" },
                    ["Fingerprint"] = new() { S = record.Fingerprint },
                    ["Data"] = new() { M = record.Data.ToDictionary(p => p.Key, p => new AttributeValue { S = p.Value }) }
                }
            }, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            var previous = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                ConsistentRead = true,
                Key = new Dictionary<string, AttributeValue> { ["EventKey"] = new() { S = record.EventKey } }
            }, cancellationToken);
            if (previous.Item is not null &&
                previous.Item.TryGetValue("ProcessingStatus", out var state) && state.S == "Completed" &&
                previous.Item.TryGetValue("Fingerprint", out var fingerprint) && fingerprint.S == record.Fingerprint)
                return false;
            // Never silently acknowledge an incomplete/conflicting item left by another implementation.
            throw new InvalidOperationException("Existing event is incomplete or conflicts with this event.");
        }
    }
}
