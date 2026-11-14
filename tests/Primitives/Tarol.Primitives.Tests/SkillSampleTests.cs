using Tarol.Primitives.Tests.SkillSample;
using Tarol.Results;
using Tarol.StronglyTypedId;

namespace Tarol.Primitives.Tests
{
    /// <summary>The example of skills/tarol-primitives/SKILL.md, compiled and run (every API name in the skill must compile).</summary>
    public class SkillSampleTests
    {
        [Fact]
        public void The_Skill_Example_Works()
        {
            var outcome = AggregateTest.Given(Order.Start()).When(static o => o.Submit(DateTimeOffset.UnixEpoch));
            outcome.Result.ShouldBe(Result.Failure(Error.Conflict("Order.Empty")));
            outcome.Events.ShouldBeEmpty();

            var order = Order.Start();
            var line = order.Add("SKU", 0).GetValueOrThrow();
            AggregateChanges.Prepare(order).ShouldBe(Result.Failure(Error.Conflict("Line.Quantity")));
            line.Remove();
            order.Submit(DateTimeOffset.UnixEpoch).ShouldBe(Result.Failure(Error.Conflict("Order.Empty")));
            order.Add("SKU", 2).IsSuccess.ShouldBeTrue();
            order.Submit(DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();
            order.Add("SKU", 1).ShouldBe(Result<Line>.Failure(Error.Conflict("Order.Submitted")));
        }

        [Fact]
        public void The_Skill_State_Example_Works()
        {
            var options = System.Text.Json.JsonSerializerOptions.Default;
            var order = Order.Start();
            order.Add("SKU", 2);
            byte[] json = AggregateState.Write(order, options);
            Order loaded = AggregateState.Read(order.Id, json, options).GetValueOrThrow();
            AggregateTest.StoredStateMatchesReplay(order, options);

            loaded.Lines.Single().Quantity.ShouldBe(2);
            System.Text.Encoding.UTF8.GetString(json).ShouldStartWith($$"""{"id":"{{order.Id.Value}}","version":2,"schema":""");
        }
    }
}

namespace Tarol.Primitives.Tests.SkillSample
{
    public partial class Order : AggregateRoot<Order>
    {
        public EntitySet<Line> Lines { get; } = [];
        public bool IsOpen { get; private set; }
        public bool Submitted { get; private set; }

        public static Order Start() { var o = new Order(Id<Order>.New()); o.Raise(new Started()); return o; }
        public Result<Line> Add(string sku, int quantity)
            => Submitted ? Error.Conflict("Order.Submitted") : Raise(new Line.Added(sku, quantity));
        public Result Submit(DateTimeOffset now) => Lines.Count == 0 ? Error.Conflict("Order.Empty") : Raise(new SubmittedAt(now));

        public sealed record Started : Event;
        public sealed record SubmittedAt(DateTimeOffset At) : Event;
        private void On(Started e) => IsOpen = true;
        private void On(SubmittedAt e) => Submitted = true;
    }

    public partial class Line : Entity<Order, Line>
    {
        public string Sku { get; private set; } = "";
        public int Quantity { get; private set; }
        public Success Remove() => Raise(new Removed());

        public sealed record Added(string Sku, int Quantity) : Created;
        public sealed record Removed : Deleted;
        private void On(Added e) => (Sku, Quantity) = (e.Sku, e.Quantity);
        [Invariant] private Result Positive() => Quantity > 0 ? Result.Success() : Error.Conflict("Line.Quantity");
    }
}
