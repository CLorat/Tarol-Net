namespace Tarol.Primitives;

/// <summary>
/// Marks an aggregate root of which each tenant has exactly one (TN-2): the facility that is the tenant, its settings, its
/// invoice number range. Its id is the tenant's id (<c>Id&lt;T&gt;.From(tenant.Value)</c>); the change pipeline loads it from
/// the caller's tenant, so a change needs no <c>Id&lt;T&gt;</c> member and an HTTP entry no <c>{id}</c> route value
/// (<c>PUT /settings/name</c>, <c>GET /settings</c>). A creation must create it with that id.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PerTenantAttribute : Attribute;
