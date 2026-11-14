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
book.Lend("Ana");
Check("generated dispatch from the package", Shelf.Rehydrate(shelf.Id, shelf.UncommittedChanges).Books.Single().Borrower == "Ana");
Check("invariants from the package", AggregateChanges.Prepare(shelf).IsSuccess);
Check("given/when/then helper", AggregateTest.Given(Shelf.Create()).When(static s => Result.Success(s.Add("X"))).Events.Count == 1);

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

    public partial class Book : Entity<Shelf, Book>
    {
        public string Title { get; private set; } = "";
        public string? Borrower { get; private set; }

        public Success Lend(string borrower) => Raise(new Lent(borrower));

        public sealed record Added(string Title) : Created;
        public sealed record Lent(string Borrower) : Event;

        private void On(Added e) => Title = e.Title;
        private void On(Lent e) => Borrower = e.Borrower;
    }
}
