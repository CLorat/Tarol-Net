namespace Tarol.Results.Tests;

// Ported from Tarol Error.UnitTests.cs (7 tests, 18 cases); the old Violation/ViolationType became Invalid | Error + ErrorType.
public class ViolationTests
{
    [Fact] // Tarol: None_ShouldExposeEmptyCodeAndDefaultType (Violation.None is gone; its replacement for "no result" is Uninitialized)
    public void Uninitialized_Should_BeAnInternalErrorWithAStableCode()
    {
        Error.Uninitialized.Type.ShouldBe(ErrorType.Internal);
        Error.Uninitialized.Code.ShouldBe("Result.Uninitialized");
        Error.Uninitialized.ToHttpStatusCode().ShouldBe(500);
        default(ErrorType).ShouldBe(ErrorType.Internal);
    }

    [Fact] // Tarol: Validation_ShouldPopulateTypeAndProperty
    public void Field_Should_PopulateFieldAndCode()
    {
        var invalid = Invalid.Field("Email", "User.EmailInvalid", "not an address");

        invalid.Errors.ShouldHaveSingleItem().ShouldBe(new FieldError("Email", "User.EmailInvalid", "not an address"));
        invalid.ToHttpStatusCode().ShouldBe(400);
    }

    [Fact] // Tarol: NotFound_Generic_ShouldIncludeEntityName
    public void NotFound_Generic_Should_IncludeEntityNameAndId()
    {
        var id = Guid.Parse("0190c5d2-0000-7000-8000-000000000001");

        var error = Error.NotFound<Order, Guid>(id);

        error.Type.ShouldBe(ErrorType.NotFound);
        error.Code.ShouldBe("Order.NotFound");
        error.Detail.ShouldBe("Order '0190c5d2-0000-7000-8000-000000000001' was not found.");
    }

    [Fact] // Tarol: Internal_ShouldPreserveDetail
    public void Internal_Should_PreserveDetail()
    {
        var error = Error.Internal("Database.Down", "primary unreachable");

        error.Type.ShouldBe(ErrorType.Internal);
        error.Detail.ShouldBe("primary unreachable");
    }

    [Fact] // Tarol: Aggregate_ShouldCaptureInnerErrors (aggregation is now Invalid with several field errors)
    public void Invalid_Should_KeepAllFieldErrorsInOrder()
    {
        var invalid = new Invalid(new FieldError("Email", "Email.Required"), new FieldError("Password", "Password.TooShort"));

        invalid.Errors.Select(static e => e.Code).ShouldBe(["Email.Required", "Password.TooShort"]);
    }

    [Theory] // Tarol: ToHttpStatusCode_ShouldMapKnownTypes (Validation -> Invalid, Custom removed)
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Gone, 410)]
    [InlineData(ErrorType.UnprocessableEntity, 422)]
    [InlineData(ErrorType.TooManyRequests, 429)]
    [InlineData(ErrorType.Internal, 500)]
    [InlineData(ErrorType.Unavailable, 503)]
    [InlineData(ErrorType.Timeout, 504)]
    public void ToHttpStatusCode_Should_MapKnownTypes(ErrorType type, int expected)
    {
        type.ToHttpStatusCode().ShouldBe(expected);
        new Error(type, "X.Y").ToHttpStatusCode().ShouldBe(expected);
    }

    [Fact] // Tarol: ToHttpStatusCode_ShouldFallbackTo500OnUnknownType
    public void ToHttpStatusCode_Should_FallBackTo500_When_TypeIsUnknown() => ((ErrorType)200).ToHttpStatusCode().ShouldBe(500);

    [Fact]
    public void Factories_Should_SetTheirType()
    {
        Error.Conflict("c").Type.ShouldBe(ErrorType.Conflict);
        Error.Forbidden().Code.ShouldBe("Auth.Forbidden");
        Error.Unauthorized().Code.ShouldBe("Auth.Unauthorized");
        Error.Gone("g").Type.ShouldBe(ErrorType.Gone);
        Error.UnprocessableEntity("u").Type.ShouldBe(ErrorType.UnprocessableEntity);
        Error.TooManyRequests("t").Type.ShouldBe(ErrorType.TooManyRequests);
        Error.Unavailable("u").Type.ShouldBe(ErrorType.Unavailable);
        Error.Timeout("t").Type.ShouldBe(ErrorType.Timeout);
        Error.NotFound("n").Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public void Constructors_Should_RejectMissingCodesAndEmptyLists()
    {
        Should.Throw<ArgumentException>(static () => new Error(ErrorType.NotFound, ""));
        Should.Throw<ArgumentException>(static () => new FieldError("Name", ""));
        Should.Throw<ArgumentNullException>(static () => new FieldError(null!, "Name.Required"));
        Should.Throw<ArgumentException>(static () => new Invalid(Array.Empty<FieldError>()));
        Should.Throw<ArgumentNullException>(static () => new Invalid([null!]));
    }

    [Fact]
    public void Invalid_Should_CopyItsList()
    {
        var list = new List<FieldError> { new("Name", "Name.Required") };
        var invalid = new Invalid(list);

        list.Add(new FieldError("Email", "Email.Invalid"));

        invalid.Errors.Count.ShouldBe(1);
    }

    [Fact]
    public void Invalid_Combine_Should_ConcatenateFieldErrors()
        => Invalid.Field("A", "A.X").Combine(Invalid.Field("B", "B.X")).ShouldBe(new Invalid(new FieldError("A", "A.X"), new FieldError("B", "B.X")));

    [Fact] // R-6: equality of aggregates was by reference
    public void Equality_Should_CompareFieldErrorsElementWise()
    {
        Violation a = new Invalid([new FieldError("Name", "Name.Required")]);
        Violation b = new Invalid([new FieldError("Name", "Name.Required")]);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(Invalid.Field("Name", "Name.TooLong"));
        Error.NotFound("x").ShouldBe(Error.NotFound("x"));
        Error.NotFound("x").ShouldNotBe<Violation>(Error.Conflict("x"));
    }

    [Fact]
    public void ToString_Should_BeReadable()
    {
        new Invalid(new FieldError("Name", "Name.Required"), new FieldError("Email", "Email.Invalid")).ToString()
            .ShouldBe("Invalid { Name: Name.Required; Email: Email.Invalid }");
        Error.NotFound("User.NotFound").ToString().ShouldBe("Error { NotFound, User.NotFound }");
    }

    [Fact] // closed hierarchy: a switch over the two kinds needs no default arm (a missing one is CS8509, an error here)
    public void Switch_Over_Kinds_Should_BeExhaustive()
    {
        static int Status(Violation violation) => violation switch
        {
            Invalid => 400,
            Error error => error.ToHttpStatusCode(),
        };

        Status(Invalid.Field("a", "b")).ShouldBe(400);
        Status(Error.Conflict("c")).ShouldBe(409);
    }

    private sealed class Order;
}
