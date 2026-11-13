using System.Reflection;

namespace Tarol.Kernel.Tests;

// One test per finding of docs/migration/kernel.md §2 that the Kernel itself fixes.
public class FindingTests
{
    private static readonly Assembly Kernel = typeof(OperationContext).Assembly;

    [Fact] // 14 and K-1: no global or ambient clock (DomainClock, Tarol.Clock) exists to be swapped
    public void F14_Kernel_Should_HaveNoSharedClock()
    {
        var shared = Kernel.GetTypes()
            .SelectMany(static t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => (t, f.FieldType)))
            .Where(static x => x.FieldType == typeof(TimeProvider) || x.FieldType.IsGenericType && x.FieldType.GetGenericTypeDefinition() == typeof(AsyncLocal<>))
            .Select(static x => x.t.FullName)
            .ToArray();

        shared.ShouldBeEmpty();
    }

    [Fact] // 26 and K-1: every public type is in a Tarol.Kernel namespace (the old IAuthorizer was in `Security`, Clock in `Tarol`)
    public void F26_PublicTypes_Should_BeInTheKernelNamespace()
        => Kernel.GetExportedTypes().Where(static t => t.Namespace is not ("Tarol.Kernel" or "Tarol.Kernel.Json")).ShouldBeEmpty();

    [Fact] // K-6: the recorded context holds no authorization input (the old IUserContext carried Roles)
    public void K6_Context_Should_HoldNoRoles()
    {
        typeof(OperationContext).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(static p => p.Name).ShouldBe(["Actor", "Tenant", "CorrelationId", "CausationId"], ignoreOrder: true);
        typeof(Actor).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(static p => p.Name).ShouldBe(["Kind", "Id", "OnBehalfOf"], ignoreOrder: true);
    }

    [Fact] // the old IAuthorizer and IUserContext/ContextUserType are not carried over
    public void Old_Types_Should_BeGone()
    {
        Kernel.GetExportedTypes().Select(static t => t.Name).ShouldNotContain("IAuthorizer");
        Kernel.GetExportedTypes().Select(static t => t.Name).ShouldNotContain("IUserContext");
        Kernel.GetExportedTypes().Select(static t => t.Name).ShouldNotContain("IUser");
        Kernel.GetExportedTypes().Select(static t => t.Name).ShouldNotContain("ITrace");
    }
}
