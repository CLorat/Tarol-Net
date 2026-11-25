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

[Projection("recipe-list")]
public sealed partial record RecipeRow(Guid Id, string Title, int Servings)
{
    public static RecipeRow When(Recipe.Written e, RecordedEvent r) => new(r.StreamId, e.Title, 0);

    public RecipeRow When(Recipe.Scaled e) => this with { Servings = e.Servings };
}

[Endpoint("GET /recipes")]
public sealed record RecipeSearch(string? Title, int? Servings) : IQuery<RecipeRow>;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WriteRecipe)), JsonSerializable(typeof(ScaleRecipe)), JsonSerializable(typeof(RecipeRow)), JsonSerializable(typeof(LabelRecipe))]
[JsonSerializable(typeof(Recipe.Written)), JsonSerializable(typeof(Recipe.Scaled))]
public sealed partial class KitchenJson : JsonSerializerContext;
