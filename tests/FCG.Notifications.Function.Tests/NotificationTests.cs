using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.SQSEvents;
using Amazon.Runtime;
using FCG.IntegrationEvents.V1;
using FCG.Notifications.Function;

namespace FCG.Notifications.Function.Tests;

internal static class TestHandlerExtensions
{
    public static Task<SQSBatchResponse> HandleForTestAsync(this NotificationHandler handler, SQSEvent input) =>
        handler.HandleAsync(input, TestContext.Current.CancellationToken);
}

public sealed class NotificationTests
{
    private const string UsersQueue = "arn:aws:sqs:us-east-1:123456789012:fcg-fase3-user-created";
    private const string PaymentsQueue = "arn:aws:sqs:us-east-1:123456789012:fcg-fase3-payment-processed";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static UserCreatedEvent UserEvent() => new(
        Guid.NewGuid(), DateTime.UtcNow, UserId, "Test Person", "person@example.com");
    private static PaymentProcessedEvent Payment(string status) => new(
        Guid.NewGuid(), DateTime.UtcNow, UserId, "person@example.com", Guid.NewGuid(), 10, status);

    private static SQSEvent.SQSMessage Message(object body, string queue, string id = "m1") => new()
    {
        MessageId = id,
        EventSourceArn = queue,
        Body = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };
    private static SQSEvent Batch(params SQSEvent.SQSMessage[] messages) => new() { Records = messages.ToList() };
    private static NotificationHandler Handler(INotificationStore store, List<string>? logs = null) =>
        new(store, UsersQueue, PaymentsQueue, line => logs?.Add(line));

    [Fact]
    public async Task Welcome_is_recorded_once_for_redelivery()
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var original = UserEvent();
        Assert.Empty((await handler.HandleForTestAsync(Batch(Message(original, UsersQueue)))).BatchItemFailures);
        Assert.Empty((await handler.HandleForTestAsync(Batch(Message(original, UsersQueue, "redelivery")))).BatchItemFailures);
        var row = Assert.Single(db.Rows).Value;
        Assert.Equal("Welcome", row["NotificationKind"].S);
        Assert.Equal("Simulated", row["NotificationState"].S);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Theory]
    [InlineData("Approved", "PurchaseConfirmation", "Simulated")]
    [InlineData("approved", "PurchaseConfirmation", "Simulated")]
    [InlineData("Rejected", "None", "Skipped")]
    public async Task Preserves_payment_rules(string status, string kind, string state)
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        Assert.Empty((await handler.HandleForTestAsync(Batch(Message(Payment(status), PaymentsQueue)))).BatchItemFailures);
        var row = Assert.Single(db.Rows).Value;
        Assert.Equal(kind, row["NotificationKind"].S);
        Assert.Equal(state, row["NotificationState"].S);
    }

    [Fact]
    public async Task Payment_timestamp_can_change_on_retry_without_duplicate()
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var original = Payment("Approved");
        await handler.HandleForTestAsync(Batch(Message(original, PaymentsQueue)));
        var result = await handler.HandleForTestAsync(Batch(Message(original with { ProcessedAtUtc = DateTime.UtcNow.AddMinutes(1) }, PaymentsQueue)));
        Assert.Empty(result.BatchItemFailures);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Fact]
    public async Task Conflicting_redelivery_is_retried_instead_of_silently_acknowledged()
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var original = Payment("Approved");
        await handler.HandleForTestAsync(Batch(Message(original, PaymentsQueue)));
        var result = await handler.HandleForTestAsync(Batch(Message(original with { Status = "Rejected" }, PaymentsQueue)));
        Assert.Equal("m1", Assert.Single(result.BatchItemFailures).ItemIdentifier);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Invalid_payload_retries_only_failed_record(string body)
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var bad = Message(UserEvent(), UsersQueue, "bad");
        bad.Body = body;
        var result = await handler.HandleForTestAsync(Batch(bad, Message(UserEvent(), UsersQueue, "good")));
        Assert.Equal("bad", Assert.Single(result.BatchItemFailures).ItemIdentifier);
        Assert.Single(db.Rows);
    }

    [Theory]
    [InlineData("Unknown", 10)]
    [InlineData("Approved", 0)]
    public async Task Invalid_business_payload_does_not_create_simulation(string status, int price)
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var result = await handler.HandleForTestAsync(Batch(Message(Payment(status) with { Price = price }, PaymentsQueue)));
        Assert.Single(result.BatchItemFailures);
        Assert.Empty(db.Rows);
    }

    [Fact]
    public async Task Unknown_source_is_retried()
    {
        using var db = new MemoryDynamo();
        var result = await Handler(new DynamoNotificationStore(db, "events"))
            .HandleForTestAsync(Batch(Message(UserEvent(), "unexpected")));
        Assert.Single(result.BatchItemFailures);
        Assert.Empty(db.Rows);
    }

    [Fact]
    public async Task Failure_before_write_can_be_retried_without_losing_notification()
    {
        using var db = new MemoryDynamo { FailBeforeWrite = true };
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var batch = Batch(Message(UserEvent(), UsersQueue));
        Assert.Single((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        Assert.Empty(db.Rows);
        Assert.Empty((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Fact]
    public async Task Lost_response_after_commit_does_not_duplicate_simulation()
    {
        using var db = new MemoryDynamo { FailAfterWrite = true };
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var batch = Batch(Message(UserEvent(), UsersQueue));
        Assert.Single((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        Assert.Single(db.Rows);
        Assert.Empty((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Fact]
    public async Task Existing_incomplete_record_is_not_treated_as_success()
    {
        using var db = new MemoryDynamo();
        var original = UserEvent();
        db.Rows[$"user-created:{original.EventId:D}"] = new()
        {
            ["ProcessingStatus"] = new() { S = "Processing" }
        };
        var result = await Handler(new DynamoNotificationStore(db, "events"))
            .HandleForTestAsync(Batch(Message(original, UsersQueue)));
        Assert.Single(result.BatchItemFailures);
    }

    [Fact]
    public async Task Unavailable_duplicate_read_is_retried()
    {
        using var db = new MemoryDynamo();
        var handler = Handler(new DynamoNotificationStore(db, "events"));
        var batch = Batch(Message(UserEvent(), UsersQueue));
        await handler.HandleForTestAsync(batch);
        db.FailRead = true;
        Assert.Single((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        db.FailRead = false;
        Assert.Empty((await handler.HandleForTestAsync(batch)).BatchItemFailures);
        Assert.Equal(1, db.SuccessfulWrites);
    }

    [Fact]
    public async Task Logs_and_store_omit_name_and_email()
    {
        using var db = new MemoryDynamo();
        var logs = new List<string>();
        var original = UserEvent();
        await Handler(new DynamoNotificationStore(db, "events"), logs)
            .HandleForTestAsync(Batch(Message(original, UsersQueue)));
        var output = string.Join("", logs) + JsonSerializer.Serialize(db.Rows);
        Assert.DoesNotContain(original.Email, output);
        Assert.DoesNotContain(original.Name, output);
        Assert.Contains("SimulationRecorded", output);
    }

    [Fact]
    public async Task Cancellation_keeps_all_unprocessed_messages_for_retry()
    {
        using var db = new MemoryDynamo();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Handler(new DynamoNotificationStore(db, "events"))
            .HandleAsync(Batch(Message(UserEvent(), UsersQueue, "one"), Message(UserEvent(), UsersQueue, "two")), cancellation.Token);
        Assert.Equal(2, result.BatchItemFailures.Count);
        Assert.Empty(db.Rows);
    }

    private sealed class MemoryDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public Dictionary<string, Dictionary<string, AttributeValue>> Rows { get; } = new();
        public bool FailBeforeWrite { get; set; }
        public bool FailAfterWrite { get; set; }
        public bool FailRead { get; set; }
        public int SuccessfulWrites { get; private set; }

        public override Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailBeforeWrite) { FailBeforeWrite = false; throw new IOException("Store unavailable"); }
            Assert.Equal("attribute_not_exists(EventKey)", request.ConditionExpression);
            if (!Rows.TryAdd(request.Item["EventKey"].S, request.Item))
                throw new ConditionalCheckFailedException("Duplicate");
            SuccessfulWrites++;
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("Response lost"); }
            return Task.FromResult(new PutItemResponse());
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("Store unavailable");
            Assert.True(request.ConsistentRead);
            Rows.TryGetValue(request.Key["EventKey"].S, out var item);
            return Task.FromResult(new GetItemResponse { Item = item });
        }
    }
}
