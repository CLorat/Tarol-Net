namespace Tarol.Results.Tests;

// Option<T> API. Tests ported from CLorat/Tarol@6a62f81 tests/Options/Options carry the old name in a comment.
public class OptionTests
{
    public sealed record User(string Name, int Age);

    public readonly record struct UserId(Guid Value);

    // ---- creation and checks: the same code for value and reference types (O-2)

    [Fact] // Class_Some_Should_Work_And_Guard_Null
    public void Some_Should_HoldTheValue_ForAReferenceType()
    {
        var option = Option.Some(new User("Alice", 30));

        option.IsSome.ShouldBeTrue();
        option.GetValueOrThrow().Name.ShouldBe("Alice");
        Should.Throw<ArgumentNullException>(() => Option.Some<User>(null!));
    }

    [Fact] // Some_Should_HaveValue_And_CorrectData
    public void Some_Should_HoldTheValue_ForAValueType()
    {
        Option<int> option = Option.Some(42);

        option.IsSome.ShouldBeTrue();
        option.IsNone.ShouldBeFalse();
        (option is 42).ShouldBeTrue();
    }

    [Fact] // Class_None_Should_Be_Null_Reference (None is now a case, not a null reference)
    public void None_Should_BeAbsent_ForAReferenceType()
    {
        Option<User> option = Option.None;

        option.IsNone.ShouldBeTrue();
        (option switch { User => "some", None => "none" }).ShouldBe("none");
    }

    [Fact] // None_Should_BeNull_And_IsNone
    public void None_Should_BeAbsent_ForAValueType()
    {
        Option<int> option = Option.None;

        option.IsSome.ShouldBeFalse();
        option.IsNone.ShouldBeTrue();
        option.ShouldBe(default(Option<int>));
    }

    [Fact]
    public void Default_Should_BeNone() // Q-3
    {
        default(Option<int>).IsNone.ShouldBeTrue();
        default(Option<User>).IsNone.ShouldBeTrue();
        default(Option<UserId>).ShouldBe(Option.None);
    }

    [Fact]
    public void SameCode_Should_WorkForIntGuidIdStringAndUser()
    {
        static string Describe<T>(Option<T> option) where T : notnull => option switch { T value => $"some {value}", None => "none" };

        Describe(Option.Some(1)).ShouldBe("some 1");
        Describe(Option.Some(Guid.Empty)).ShouldBe("some 00000000-0000-0000-0000-000000000000");
        Describe(Option.Some(new UserId(Guid.Empty))).ShouldStartWith("some UserId");
        Describe(Option.Some("a")).ShouldBe("some a");
        Describe(Option.Some(new User("Ada", 1))).ShouldStartWith("some User");
        Describe(default(Option<int>)).ShouldBe("none");
        Describe(default(Option<User>)).ShouldBe("none");
    }

    // ---- null (Q-3, N-6)

    [Fact]
    public void Null_Should_Throw_WhenUsedAsAValue()
    {
        Should.Throw<ArgumentNullException>(() => new Option<string>(null!));
        Should.Throw<ArgumentNullException>(() =>
        {
            Option<string> option = (string)null!;
            return option;
        });
    }

    [Fact] // N-6: Nullable<T> passed the reference-type null check
    public void NullableValueType_Should_Throw_WhenNull()
    {
#pragma warning disable CS8714 // the test checks the run-time guard behind the compiler warning
        Should.Throw<ArgumentNullException>(() => new Option<int?>(null));
        new Option<int?>(5).IsSome.ShouldBeTrue();
#pragma warning restore CS8714
    }

    [Fact]
    public void ToOption_Should_MapNullToNone_ForBothKinds()
    {
        ((string?)null).ToOption().IsNone.ShouldBeTrue();
        "a".ToOption().ShouldBe(Option.Some("a"));
        ((int?)null).ToOption().IsNone.ShouldBeTrue();
        ((int?)3).ToOption().ShouldBe(Option.Some(3));
    }

    [Fact]
    public void ToNullable_Should_UnwrapToTheNullableOfEachKind()
    {
        int? number = Option.Some(3).ToNullable();
        int? none = default(Option<int>).ToNullable();
        string? text = Option.Some("a").ToNullable();
        string? noText = default(Option<string>).ToNullable();

        number.ShouldBe(3);
        none.ShouldBeNull();
        text.ShouldBe("a");
        noText.ShouldBeNull();
    }

    // ---- unwrap and fall back (O-3: the old struct tests asserted Nullable<T>.Value from the BCL)

    [Fact] // ValueOrThrow_Should_Return_Value_When_Some
    public void GetValueOrThrow_Should_ReturnTheValue()
        => Option.Some(100).GetValueOrThrow().ShouldBe(100);

    [Fact] // Value_Property_Should_Throw_Custom_Exception_On_None (class and struct versions); replaces ValueOrThrow_Should_Throw_When_None
    public void GetValueOrThrow_Should_Throw_OnNone()
    {
        Should.Throw<InvalidOperationException>(() => default(Option<User>).GetValueOrThrow()).Message.ShouldContain("None");
        Should.Throw<InvalidOperationException>(() => default(Option<int>).GetValueOrThrow()).Message.ShouldContain("None");
    }

    [Fact] // Or_Should_Fallback_Correctly
    public void GetValueOrDefault_Should_FallBack()
    {
        default(Option<int>).GetValueOrDefault(50).ShouldBe(50);
        Option.Some(20).GetValueOrDefault(50).ShouldBe(20);
        default(Option<string>).GetValueOrDefault(static () => "lazy").ShouldBe("lazy");
        Option.Some("a").GetValueOrDefault(static () => throw new InvalidOperationException("not called")).ShouldBe("a");
    }

    [Fact]
    public void TryGetValue_Should_ReportTheCase()
    {
        Option.Some(7).TryGetValue(out int value).ShouldBeTrue();
        value.ShouldBe(7);
        default(Option<int>).TryGetValue(out int none).ShouldBeFalse();
        none.ShouldBe(0);
        default(Option<int>).TryGetValue(out None _).ShouldBeTrue();
    }

    // ---- combinators

    [Fact] // Match_Should_Execute_Correct_Branch
    public void Match_Should_CallTheMatchingBranch()
    {
        Option.Some(5).Match(static v => v * 2, static () => 0).ShouldBe(10);
        default(Option<int>).Match(static v => v * 2, static () => 0).ShouldBe(0);
    }

    [Fact] // Map_Should_Transform_Only_When_Some
    public void Map_Should_TransformOnlyWhenSome()
    {
        Option.Some(10).Map(static v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(Option.Some("10"));
        default(Option<int>).Map(static v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).IsNone.ShouldBeTrue();
    }

    [Fact] // Map_Struct_To_Class_Should_Work
    public void Map_Should_GoFromAValueTypeToAReferenceType()
        => Option.Some(25).Map(static a => new User("Generated", 1)).GetValueOrThrow().Age.ShouldBe(1);

    [Fact] // Map_Class_To_Struct_Should_Work
    public void Map_Should_GoFromAReferenceTypeToAValueType()
        => Option.Some(new User("Bob", 42)).Map(static u => u.Age).ShouldBe(Option.Some(42));

    [Fact] // Map_None_Class_To_Struct_Should_Be_Null_Nullable
    public void Map_Should_KeepNone_AcrossKinds()
        => default(Option<User>).Map(static u => u.Age).IsNone.ShouldBeTrue();

    [Fact] // Bind_Should_Flatten_Results_Correctly
    public void Bind_Should_Flatten()
    {
        var result = Option.Some(10).Bind(static input => input == 10 ? Option.Some(new User("Found", input)) : Option.None);

        result.GetValueOrThrow().Name.ShouldBe("Found");
        Option.Some(11).Bind(static input => input == 10 ? Option.Some(new User("Found", input)) : Option.None).IsNone.ShouldBeTrue();
    }

    [Fact] // Bind_None_Class_To_Struct_Should_Be_Null_Nullable
    public void Bind_Should_KeepNone()
        => default(Option<User>).Bind(static u => Option.Some(u.Age)).IsNone.ShouldBeTrue();

    [Fact] // Filter_Should_Return_None_If_Predicate_Fails
    public void Filter_Should_DropTheValue_WhenThePredicateFails()
    {
        Option<int> age = 15;

        age.Filter(static x => x > 10).IsSome.ShouldBeTrue();
        age.Filter(static x => x > 20).IsNone.ShouldBeTrue();
        default(Option<int>).Filter(static _ => true).IsNone.ShouldBeTrue();
    }

    [Fact]
    public void Combinators_Should_RejectNullDelegates()
    {
        var option = Option.Some(1);
        Should.Throw<ArgumentNullException>(() => option.Map<int>(null!));
        Should.Throw<ArgumentNullException>(() => option.Bind<int>(null!));
        Should.Throw<ArgumentNullException>(() => option.Filter(null!));
        Should.Throw<ArgumentNullException>(() => option.Match(null!, static () => 0));
        Should.Throw<ArgumentNullException>(() => option.GetValueOrDefault((Func<int>)null!));
    }

    // ---- Results (one call each way)

    [Fact]
    public void ToResult_Should_TurnNoneIntoTheViolation()
    {
        var notFound = Error.NotFound("User.NotFound");

        Option.Some(new User("Ada", 1)).ToResult(notFound).IsSuccess.ShouldBeTrue();
        default(Option<User>).ToResult(notFound).ShouldBe(Result<User>.Failure(notFound));
        (default(Option<int>).ToResult(static () => Error.NotFound("Int.NotFound")) is Error { Code: "Int.NotFound" }).ShouldBeTrue();
        Option.Some(1).ToResult(static () => throw new InvalidOperationException("not called")).ShouldBe(Result.Success(1));
    }

    [Fact]
    public void ToOption_Should_DropTheViolation()
    {
        Result.Success(3).ToOption().ShouldBe(Option.Some(3));
        Result<int>.Failure(Invalid.Field("A", "A.X")).ToOption().IsNone.ShouldBeTrue();
        default(Result<int>).ToOption().IsNone.ShouldBeTrue();
    }

    // ---- equality and text

    [Fact]
    public void Equality_Should_CompareCasesAndValues()
    {
        (Option.Some(1) == Option.Some(1)).ShouldBeTrue();
        (Option.Some(1) != Option.Some(2)).ShouldBeTrue();
        (Option.Some(1) == default).ShouldBeFalse();
        ((Option<string>)Option.None == default(Option<string>)).ShouldBeTrue();
        Option.Some("a").GetHashCode().ShouldBe(Option.Some("a").GetHashCode());
        Option.Some(1).Equals((object)Option.Some(1)).ShouldBeTrue();
        default(None).ShouldBe(Option.None);
        (Option.None == default).ShouldBeTrue();
    }

    [Fact]
    public void ToString_Should_NameTheCase()
    {
        Option.Some(42).ToString().ShouldBe("Some(42)");
        default(Option<int>).ToString().ShouldBe("None");
        Option.None.ToString().ShouldBe("None");
    }
}
