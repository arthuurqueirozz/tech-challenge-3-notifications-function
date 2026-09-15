using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FCG.Notifications.Function;

// The durable simulated notification IS the effect; CloudWatch is its diagnostic projection.
public sealed record NotificationRecord(
    string EventKey, string EventType, string NotificationKind,
    string OccurredAtUtc, IReadOnlyDictionary<string, string> Data)
{
    // Payment ProcessedAtUtc can change when the existing payment consumer retries.
    // It must not change the event identity or the semantic duplicate comparison.
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            EventType,
            NotificationKind,
            Data = Data.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        }))));
}

public interface INotificationStore
{
    // Returns true for a new atomic simulation; false for the same completed event.
    Task<bool> RecordSimulationAsync(NotificationRecord record, CancellationToken cancellationToken);
}
