namespace Tarol.Kernel;

/// <summary>
/// Phantom type for <c>Id&lt;ITenant&gt;</c>: the tenant an operation belongs to. An interface, so that it never clashes
/// with an application's own <c>Tenant</c> aggregate.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A phantom type argument for Id<T> (docs/migration/kernel.md decision 3); it is never implemented.")]
public interface ITenant;

/// <summary>Phantom type for <c>Id&lt;ICorrelation&gt;</c>: the conversation an operation belongs to (a request, a job, a saga).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A phantom type argument for Id<T> (docs/migration/kernel.md decision 3); it is never implemented.")]
public interface ICorrelation;

/// <summary>Phantom type for <c>Id&lt;ICausation&gt;</c>: the id of the command or message that caused an operation.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A phantom type argument for Id<T> (docs/migration/kernel.md decision 3); it is never implemented.")]
public interface ICausation;
