// Replayer decisions that protect against data loss: what is retried, what is skipped, when to move on.
// (Replayer: lỗi nào gửi lại, lỗi nào bỏ qua, khi nào được đi tiếp.)
using ShipmentSla.Replayer;
using Xunit;

namespace ShipmentSla.Service.Tests;

public class ReplayerRulesTests
{
    [Theory]
    [InlineData(200, "Sent")]
    [InlineData(202, "Sent")]
    [InlineData(400, "Rejected")]   // bad data: resending cannot help → skip, never block the replay
    [InlineData(404, "Rejected")]
    [InlineData(422, "Rejected")]
    [InlineData(401, "Failed")]     // wrong secret = configuration problem → stop and retry later
    [InlineData(403, "Failed")]
    [InlineData(429, "Failed")]     // throttled
    [InlineData(500, "Failed")]
    [InlineData(503, "Failed")]
    // PostOutcome is internal, so the expected value is its name (a public test method cannot take it).
    public void Responses_are_classified(int status, string expected) =>
        Assert.Equal(expected, Function.Classify(status).ToString());

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]   // a single temporary failure → the next run resends the same orders (no data loss)
    [InlineData(50, false)]
    public void Cursor_advances_only_without_temporary_failures(int failed, bool advance) =>
        Assert.Equal(advance, Function.ShouldAdvance(failed));

    [Fact]
    public void Backoff_doubles_and_honours_Retry_After()
    {
        Assert.InRange(Function.Backoff(1, null).TotalMilliseconds, 200, 300);
        Assert.InRange(Function.Backoff(2, null).TotalMilliseconds, 400, 500);
        Assert.InRange(Function.Backoff(4, null).TotalMilliseconds, 1600, 1700);
        Assert.Equal(TimeSpan.FromSeconds(3), Function.Backoff(1, TimeSpan.FromSeconds(3)));
    }
}
