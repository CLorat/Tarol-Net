using Tarol.Primitives;
using Tarol.Primitives.PackageTests;
using Tarol.Results;
using Tarol.StronglyTypedId;

var failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

var shelf = Shelf.Create();
var book = shelf.Add("Dune");
Check("lend through the generated guard", book.Lend("Ana").IsSuccess);
Check("generated dispatch from the package", Shelf.Rehydrate(shelf.Id, shelf.UncommittedChanges).Books.Single().Borrower == "Ana");
Check("invariants from the package", AggregateChanges.Prepare(shelf).IsSuccess);
Check("given/when/then helper", AggregateTest.Given(Shelf.Create()).When(static s => Result.Success(s.Add("X"))).Events.Count == 1);
var state = AggregateState.Write(shelf, System.Text.Json.JsonSerializerOptions.Default);
Check("stored state from the package", AggregateState.Read(shelf.Id, state, System.Text.Json.JsonSerializerOptions.Default).GetValueOrThrow().Books.Single().Borrower == "Ana");
Check("stored state == replay", AggregateTest.StoredStateMatchesReplay(shelf, System.Text.Json.JsonSerializerOptions.Default).Version == shelf.Version);

book.LoseWithoutGuard();
Check("lifecycle step check from the package", AggregateChanges.Prepare(shelf)
    == Result.Failure(Error.Internal("Lifecycle.ForbiddenStep", "Book.Status: Lent -> Lost by Lost")));
Check("lifecycle guard and diagram from the package", book.Status.CanTransitionTo(LoanStatus.Lent) == false
    && LoanStatusLifecycle.Diagram.Contains("Shelved --> Lent : Lent", StringComparison.Ordinal));
Check("lifecycle switch without a default arm (TPRMSPR01)", Book.Label(LoanStatus.Lent) == "lent");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

namespace Tarol.Primitives.PackageTests
{
    public partial class Shelf : AggregateRoot<Shelf>
    {
        public EntitySet<Book> Books { get; } = [];

        public static Shelf Create() => new(Id<Shelf>.New());

        public Book Add(string title) => Raise(new Book.Added(title));

        [Invariant]
        private Result AtMostOneHundred() => Books.Count <= 100 ? Result.Success() : Error.Conflict("Shelf.Full");
    }

    [Lifecycle]
    public enum LoanStatus
    {
        [To(Lent, Lost)] Shelved,
        [To(Shelved)] Lent,
        Lost,
    }

    public partial class Book : Entity<Shelf, Book>
    {
        public string Title { get; private set; } = "";
        public string? Borrower { get; private set; }
        public LoanStatus Status { get; private set; }

        public Result Lend(string borrower) => Status.RequireTransitionTo(LoanStatus.Lent).Bind(() => Raise(new Lent(borrower)));

        /// <summary>Forgot its guard: only a shelved book can be reported lost.</summary>
        public Success LoseWithoutGuard() => Raise(new Lost());

        public static string Label(LoanStatus status) => status switch
        {
            LoanStatus.Shelved => "shelved",
            LoanStatus.Lent => "lent",
            LoanStatus.Lost => "lost",
        };

        public sealed record Added(string Title) : Created;

        [Transition<LoanStatus>(LoanStatus.Shelved, LoanStatus.Lent)]
        public sealed record Lent(string Borrower) : Event;

        public sealed record Lost : Event;

        private void On(Added e) => Title = e.Title;

        private void On(Lent e)
        {
            Borrower = e.Borrower;
            Status = LoanStatus.Lent;
        }

        private void On(Lost e)
        {
            _ = e;
            Status = LoanStatus.Lost;
        }
    }
}
