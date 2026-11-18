namespace Tarol.Search.Generators.Tests;

internal static class Sources
{
    /// <summary>An aggregate with entities two levels deep, an entity reference, every member kind, audience and language.</summary>
    public const string Gallery = """
        using System;
        using System.Collections.Generic;
        using Tarol.Primitives;
        using Tarol.Results;
        using Tarol.Search;
        using Tarol.StronglyTypedId;

        namespace Gallery;

        public enum Medium { Oil, Watercolour }

        public sealed record Dimensions(int Width, int Height);

        [Searchable("Artwork")]
        public partial class Artwork : AggregateRoot<Artwork>
        {
            public EntitySet<Edition> Editions { get; } = [];
            public EntityRef<Frame> Frame { get; } = new();
            [SearchTitle, Searchable(SearchWeight.A)] public string Title { get; private set; } = "";
            [Searchable] public string Description { get; private set; } = "";
            [Searchable(SearchWeight.C)] public Medium Medium { get; private set; }
            [Searchable(SearchWeight.C)] public List<string> Tags { get; } = [];
            [Searchable(SearchWeight.D)] public Dimensions? Size { get; private set; }
            [Searchable(SearchWeight.D)] public int? Year { get; private set; }
            [PersonalData, Searchable(Exact = true)] public string? Owner { get; private set; }
            [SearchAudience] public string Seller { get; private set; } = "";
            [SearchLanguage] public string Language { get; private set; } = "en";
            public decimal Price { get; private set; }

            public sealed record Created(string Title, string Description, [property: PersonalData] string? Owner) : Event;
            private void On(Created e) => (Title, Description, Owner) = (e.Title, e.Description, e.Owner);
        }

        public partial class Edition : Entity<Artwork, Edition>
        {
            public EntitySet<Print> Prints { get; } = [];
            [SearchTitle, Searchable(SearchWeight.A)] public string Label { get; private set; } = "";
            public sealed record Added(string Label) : Created;
            private void On(Added e) => Label = e.Label;
        }

        public partial class Print : Entity<Artwork, Print>
        {
            [Searchable] public string Serial { get; private set; } = "";
            public sealed record Made(string Serial) : Created;
            private void On(Made e) => Serial = e.Serial;
        }

        public partial class Frame : Entity<Artwork, Frame>
        {
            [Searchable(SearchWeight.B)] public Guid Maker { get; private set; }
            public sealed record Fitted(Guid Maker) : Created;
            private void On(Fitted e) => Maker = e.Maker;
        }

        public static partial class Archive
        {
            [Searchable]
            public partial class Letter : AggregateRoot<Letter>
            {
                [Searchable(SearchWeight.A)] public string Subject { get; private set; } = "";
                [SearchAudience] public List<Guid> Readers { get; } = [];
                public sealed record Written(string Subject) : Event;
                private void On(Written e) => Subject = e.Subject;
            }
        }

        public partial class Invoice : AggregateRoot<Invoice>
        {
            [Searchable] public string Number { get; private set; } = "";
            public sealed record Issued(string Number) : Event;
            private void On(Issued e) => Number = e.Number;
        }
        """;
}
