using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;
using Tarol.Validations;

[assembly: Tarol.Changes.GenerateTarolModule("Garden")]

namespace Tarol.Changes.AspNetCore.Tests.Garden;

/// <summary>A garden plot: one bounded context of the two-assembly test application (TN-8).</summary>
public partial class Plot : AggregateRoot<Plot>
{
    public string Name { get; private set; } = "";

    public static Plot Lay(string name, DateTimeOffset now)
    {
        var plot = new Plot(Id<Plot>.New(now));
        plot.Raise(new Laid(name));
        return plot;
    }

    public Result Rename(string name) => Raise(new Renamed(name));

    public sealed record Laid(string Name) : Event;
    public sealed record Renamed(string Name) : Event;

    private void On(Laid e) => Name = e.Name;
    private void On(Renamed e) => Name = e.Name;
}

[Endpoint("POST /plots")]
public sealed partial record LayPlot(string Name) : ICreation<Plot>
{
    public Result<Plot> Execute(ChangeContext context) => Plot.Lay(Name, context.Now);
}

[Endpoint("PUT /plots/{plot}/name")]
public sealed partial record RenamePlot(Id<Plot> Plot, string Name) : IChange<Plot>
{
    public Result Execute(Plot plot, ChangeContext context) => plot.Rename(Name);
}

/// <summary>The garden's own label, 1 to 80 characters: same name as <c>Pantry.Label</c> (1 to 120), another type (Tatalu FB-INT-1).</summary>
public readonly partial record struct Label([Length(1, 80)] string Value);

/// <summary>The garden's own shade, other values than <c>Pantry.Shade</c> (Tatalu FB-INT2-1).</summary>
public enum Shade
{
    Sun,
    HalfShade,
    FullShade,
}

[Endpoint("PUT /plots/{plot}/label")]
public sealed partial record LabelPlot(Id<Plot> Plot, Label Label, Shade Shade) : IChange<Plot>, IValidatable
{
    public Result Execute(Plot plot, ChangeContext context) => plot.Rename(Label.Value);
}

/// <summary>A constant of the other assembly: both land in one x-tarol-constants.</summary>
public static class GardenRules
{
    [Tarol.Kernel.ContractConstant]
    public const bool Organic = true;
}

[Projection("plot-list")]
public sealed partial record PlotRow(Guid Id, string Name)
{
    public static PlotRow When(Plot.Laid e, RecordedEvent r) => new(r.StreamId, e.Name);

    public PlotRow When(Plot.Renamed e) => this with { Name = e.Name };
}

[Endpoint("GET /plots")]
public sealed record PlotSearch(string? Name) : IQuery<PlotRow>;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LayPlot)), JsonSerializable(typeof(RenamePlot)), JsonSerializable(typeof(PlotRow)), JsonSerializable(typeof(LabelPlot))]
[JsonSerializable(typeof(Plot.Laid)), JsonSerializable(typeof(Plot.Renamed))]
public sealed partial class GardenJson : JsonSerializerContext;
