using Tarol.Persistence;

namespace Tarol.Security;

/// <summary>
/// The role map of a <see cref="TarolModule"/> (TN-8): <c>AddSecurity</c> applies the map of every module after the application's.
/// Attach it to the generated module: <c>GeneratedModule.Module.With(new SecurityModule(DatenschutzSecurity.Map))</c>.
/// </summary>
/// <param name="Map">Maps the module's roles onto its changes and reads.</param>
public sealed record SecurityModule(Action<ChangeRoles> Map);
