// In-memory stand-in for ONE DynamoDB table, wired into an NSubstitute IAmazonDynamoDB.
// It implements only what ShipmentStore uses, with DynamoDB's real semantics for those calls:
//   GetItem, PutItem, Query ("PK = :pk", sorted by SK, paginated), TransactWriteItems (all-or-nothing,
//   CancellationReasons in request order). Only the two condition expressions the code uses are understood;
//   anything else throws NotSupportedException so a code change cannot silently pass against a wrong fake.
// (DynamoDB giả trong bộ nhớ – không cần AWS / Docker trong CI.)
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;

namespace ShipmentSla.Service.Tests;

public sealed class FakeDynamoTable
{
    // DynamoDB sorts keys by bytes (ordinal), not by culture rules → explicit ordinal comparer.
    private readonly SortedDictionary<(string Pk, string Sk), Dictionary<string, AttributeValue>> _items =
        new(Comparer<(string Pk, string Sk)>.Create((a, b) =>
            string.CompareOrdinal(a.Pk, b.Pk) is var c and not 0 ? c : string.CompareOrdinal(a.Sk, b.Sk)));

    public IAmazonDynamoDB Client { get; } = Substitute.For<IAmazonDynamoDB>();

    /// <summary>Every TransactWriteItems request, including the ones that were cancelled or failed.</summary>
    public List<TransactWriteItemsRequest> Transactions { get; } = [];

    /// <summary>Items per Query page (DynamoDB pages at 1 MB; small here to exercise pagination).</summary>
    public int QueryPageSize { get; set; } = 1000;

    /// <summary>Fault injection: return an exception to throw for this transaction (null = run normally).</summary>
    public Func<TransactWriteItemsRequest, Exception?>? FailTransaction { get; set; }

    public FakeDynamoTable()
    {
        Client.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(GetItem(ci.Arg<GetItemRequest>())));
        Client.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(PutItem(ci.Arg<PutItemRequest>())));
        Client.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Query(ci.Arg<QueryRequest>())));
        Client.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Transact(ci.Arg<TransactWriteItemsRequest>())));
    }

    public int Count => _items.Count;

    public IReadOnlyList<Dictionary<string, AttributeValue>> ItemsWithPk(string pk) =>
        _items.Where(kv => kv.Key.Pk == pk).Select(kv => kv.Value).ToList();

    public Dictionary<string, AttributeValue>? Item(string pk, string sk) =>
        _items.TryGetValue((pk, sk), out var item) ? item : null;

    /// <summary>Put an item directly (test setup), bypassing conditions.</summary>
    public void Seed(Dictionary<string, AttributeValue> item) => _items[KeyOf(item)] = Copy(item);

    // ---------------------------------------------------------------------------------------------

    private GetItemResponse GetItem(GetItemRequest r) =>
        new() { Item = _items.TryGetValue(KeyOf(r.Key), out var item) ? Copy(item) : new() };

    private PutItemResponse PutItem(PutItemRequest r)
    {
        if (!ConditionHolds(r.Item, r.ConditionExpression, r.ExpressionAttributeNames, r.ExpressionAttributeValues))
            throw new ConditionalCheckFailedException("The conditional request failed");
        _items[KeyOf(r.Item)] = Copy(r.Item);
        return new PutItemResponse();
    }

    private QueryResponse Query(QueryRequest r)
    {
        if (r.KeyConditionExpression != "PK = :pk")
            throw new NotSupportedException($"FakeDynamoTable: key condition '{r.KeyConditionExpression}'");
        var pk = r.ExpressionAttributeValues[":pk"].S;
        var all = _items.Where(kv => kv.Key.Pk == pk).ToList();             // SortedDictionary → sorted by SK
        var start = r.ExclusiveStartKey is { Count: > 0 } esk
            ? all.FindIndex(kv => kv.Key == KeyOf(esk)) + 1
            : 0;
        var page = all.Skip(start).Take(QueryPageSize).ToList();
        var more = start + page.Count < all.Count;
        return new QueryResponse
        {
            Items = page.Select(kv => Copy(kv.Value)).ToList(),
            Count = page.Count,
            LastEvaluatedKey = more ? KeyAttributes(page[^1].Key) : new()
        };
    }

    private TransactWriteItemsResponse Transact(TransactWriteItemsRequest r)
    {
        Transactions.Add(r);
        if (FailTransaction?.Invoke(r) is { } injected) throw injected;

        var reasons = new List<CancellationReason>();
        foreach (var w in r.TransactItems)
        {
            var put = w.Put ?? throw new NotSupportedException("FakeDynamoTable: only Put inside transactions");
            var ok = ConditionHolds(put.Item, put.ConditionExpression, put.ExpressionAttributeNames, put.ExpressionAttributeValues);
            reasons.Add(new CancellationReason
            {
                Code = ok ? "None" : "ConditionalCheckFailed",
                Message = ok ? null : "The conditional request failed"
            });
        }

        if (reasons.Any(x => x.Code != "None"))   // all-or-nothing: nothing is written
            throw new TransactionCanceledException("Transaction cancelled") { CancellationReasons = reasons };

        foreach (var w in r.TransactItems) _items[KeyOf(w.Put.Item)] = Copy(w.Put.Item);
        return new TransactWriteItemsResponse();
    }

    private bool ConditionHolds(Dictionary<string, AttributeValue> item, string? condition,
        Dictionary<string, string>? names, Dictionary<string, AttributeValue>? values)
    {
        _items.TryGetValue(KeyOf(item), out var existing);
        switch (condition)
        {
            case null or "":
                return true;
            case "attribute_not_exists(PK)":
                return existing is null;
            case "#v = :v":
                var attr = names!["#v"];
                return existing is not null && existing.TryGetValue(attr, out var cur) && cur.N == values![":v"].N;
            default:
                throw new NotSupportedException($"FakeDynamoTable: condition '{condition}'");
        }
    }

    private static (string, string) KeyOf(Dictionary<string, AttributeValue> item) => (item["PK"].S, item["SK"].S);

    private static Dictionary<string, AttributeValue> KeyAttributes((string Pk, string Sk) k) =>
        new() { ["PK"] = new AttributeValue { S = k.Pk }, ["SK"] = new AttributeValue { S = k.Sk } };

    private static Dictionary<string, AttributeValue> Copy(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(kv => kv.Key, kv => kv.Value);
}
