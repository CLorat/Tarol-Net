using System.Text.Json;
using System.Text.Json.Serialization;
using Tarol.Options;
using Tarol.Primitives;
using Tarol.Queries;
using Tarol.Results;
using Tarol.StronglyTypedId;

[assembly: Tarol.Changes.GenerateTarolModule]

namespace Tarol.Changes.AspNetCore.Tests.Kitchen;

/// <summary>A recipe: the other bounded context of the two-assembly test application (TN-8); its module is named after the assembly.</summary>
public partial class Recipe : AggregateRoot<Recipe>
{
    public string Title { get; private set; } = "";

    public int Servings { get; private set; }

    public static Recipe Write(string title, DateTimeOffset now)
    {
        var recipe = new Recipe(Id<Recipe>.New(now));
        recipe.Raise(new Written(title));
        return recipe;
    }

    public Result Scale(int servings) => servings < 1 ? Error.Conflict("Recipe.Servings") : Raise(new Scaled(servings));

    public sealed record Written(string Title) : Event;
    public sealed record Scaled(int Servings) : Event;

    private void On(Written e) => Title = e.Title;
    private void On(Scaled e) => Servings = e.Servings;
}

[Endpoint("POST /recipes")]
public sealed partial record WriteRecipe(string Title) : ICreation<Recipe>
{
    public Result<Recipe> Execute(ChangeContext context) => Recipe.Write(Title, context.Now);
}

[Endpoint("PUT /recipes/{recipe}/servings")]
public sealed partial record ScaleRecipe(Id<Recipe> Recipe, int Servings) : IChange<Recipe>
{
    public Result Execute(Recipe recipe, ChangeContext context) => recipe.Scale(Servings);
}

/// <summary>Contract types of an assembly without endpoints (Tatalu FB-F01.1-3, FB-INT-1).</summary>
[Endpoint("PUT /recipes/{recipe}/label")]
public sealed partial record LabelRecipe(Id<Recipe> Recipe, Pantry.Label Label, Pantry.PostalCode From, Option<Pantry.Label> Note, Pantry.Shade Shade, DayOfWeek Day) : IChange<Recipe>, Tarol.Validations.IValidatable
{
    public Result Execute(Recipe recipe, ChangeContext context) => recipe.Scale(Math.Max(1, Label.Value.Length));
}

/// <summary>A price with cents and a weight to one decimal (TN-53: [Scale] on a value object and on an input member).</summary>
[Endpoint("PUT /recipes/{recipe}/price")]
public sealed partial record PriceRecipe(Id<Recipe> Recipe, Pantry.Euro Price, [Tarol.Validations.Scale(1)] decimal Weight) : IChange<Recipe>, Tarol.Validations.IValidatable
{
    public Result Execute(Recipe recipe, ChangeContext context) => recipe.Scale(2);
}

/// <summary>More than twelve servings is a warning the cook confirms (TN-37, Tatalu FB-F04.2-2, FB-F06.2-2).</summary>
[Endpoint("PUT /recipes/{recipe}/feast")]
public sealed partial record FeastRecipe(Id<Recipe> Recipe, int Servings) : IChange<Recipe>
{
    public Result Execute(Recipe recipe, ChangeContext context)
        => Servings > 12 && !context.Confirmed("Recipe.LargeFeast")
            ? Error.ConfirmationRequired("Recipe.LargeFeast", new Dictionary<string, string> { ["servings"] = Servings.ToString(System.Globalization.CultureInfo.InvariantCulture), ["limit"] = "12" })
            : recipe.Scale(Servings);
}

/// <summary>An enum as a route value (TN-34, Tatalu FB-F17.4-5: PUT …/kanaele/{category}).</summary>
[Endpoint("PUT /recipes/{recipe}/doneness/{doneness}")]
public sealed partial record CookRecipe(Id<Recipe> Recipe, Doneness Doneness) : IChange<Recipe>
{
    public Result Execute(Recipe recipe, ChangeContext context) => recipe.Scale(Doneness == Doneness.WellDone ? 3 : 1);
}

/// <summary>A step of a plan: a nested input record (Tatalu FB-F02.1-3: a missing DateOnly read as 0001-01-01).</summary>
public sealed record PlanStep(DateOnly On, Pantry.Shade Shade, string? Note);

[Endpoint("PUT /recipes/{recipe}/plan")]
public sealed partial record PlanRecipe(Id<Recipe> Recipe, PlanStep First, IReadOnlyList<PlanStep> Steps) : IChange<Recipe>
{
    public Result Execute(Recipe recipe, ChangeContext context) => recipe.Scale(Steps.Count + 1);
}

/// <summary>An enum no operation references (Tatalu FB-F02.6-2): a contract type.</summary>
[Tarol.Kernel.ContractType]
public enum Doneness
{
    Rare,
    WellDone,
}

/// <summary>Constants the client needs (Tatalu FB-F13.5-1).</summary>
public static class KitchenRules
{
    [Tarol.Kernel.ContractConstant]
    public const int MaxServings = 12;

    [Tarol.Kernel.ContractConstant("RecipeFormat")]
    public const string Format = "v2";
}

[Projection("recipe-list")]
public sealed partial record RecipeRow(Guid Id, string Title, int Servings)
{
    public static RecipeRow When(Recipe.Written e, RecordedEvent r) => new(r.StreamId, e.Title, 0);

    public RecipeRow When(Recipe.Scaled e) => this with { Servings = e.Servings };

    /// <summary>A computed member: always written, so required in the schema (Tatalu FB-F04.6-2).</summary>
    public bool Scaled => Servings > 0;
}

[Endpoint("GET /recipes")]
public sealed record RecipeSearch(string? Title, int? Servings) : IQuery<RecipeRow>;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WriteRecipe)), JsonSerializable(typeof(ScaleRecipe)), JsonSerializable(typeof(RecipeRow)), JsonSerializable(typeof(LabelRecipe)), JsonSerializable(typeof(PlanRecipe)), JsonSerializable(typeof(PriceRecipe)), JsonSerializable(typeof(FeastRecipe)), JsonSerializable(typeof(CookRecipe)), JsonSerializable(typeof(Doneness))]
[JsonSerializable(typeof(Recipe.Written)), JsonSerializable(typeof(Recipe.Scaled))]
public sealed partial class KitchenJson : JsonSerializerContext;
