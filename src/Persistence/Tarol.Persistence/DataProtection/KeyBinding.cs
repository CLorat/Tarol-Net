using System.Buffers.Binary;

namespace Tarol.Persistence;

/// <summary>The associated data wrapped keys are bound to, so a wrapped key copied to another row does not unwrap.</summary>
public static class KeyBinding
{
    /// <summary>The associated data of a tenant key: the tenant (16 bytes).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Tenant(Guid tenant) => tenant.ToByteArray();

    /// <summary>The associated data of a data key: tenant, subject and version (36 bytes).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="version">The key's version.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Subject(Guid tenant, Guid subject, int version)
    {
        var bytes = new byte[36];
        tenant.TryWriteBytes(bytes.AsSpan(0, 16));
        subject.TryWriteBytes(bytes.AsSpan(16, 16));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), version);
        return bytes;
    }
}
