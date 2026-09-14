using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace FCG.Notifications.Function;

public sealed class Function
{
    private readonly NotificationHandler handler;

    public Function()
    {
        var table = Required("EVENTS_TABLE");
        handler = new NotificationHandler(
            new DynamoNotificationStore(new AmazonDynamoDBClient(), table),
            Required("USER_CREATED_QUEUE_ARN"),
            Required("PAYMENT_PROCESSED_QUEUE_ARN"),
            Console.WriteLine);
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent input, ILambdaContext context)
        => HandleWithDeadlineAsync(input, context.RemainingTime);

    private async Task<SQSBatchResponse> HandleWithDeadlineAsync(SQSEvent input, TimeSpan remaining)
    {
        using var deadline = new CancellationTokenSource(
            remaining > TimeSpan.FromSeconds(1) ? remaining - TimeSpan.FromSeconds(1) : TimeSpan.Zero);
        return await handler.HandleAsync(input, deadline.Token);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing configuration: {name}");
}
