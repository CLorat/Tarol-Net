// B with one name per operation needs one static class per (receiver kind x result kind): O-5 reason
namespace Probe.B2
{
    public static class CC { extension<T>(T? o) where T : class { public TOut? Map<TOut>(Func<T, TOut> f) where TOut : class => o is null ? null : f(o); } }
    public static class CS { extension<T>(T? o) where T : class { public TOut? Map<TOut>(Func<T, TOut> f) where TOut : struct => o is null ? null : f(o); } }
    public static class SC { extension<T>(T? o) where T : struct { public TOut? Map<TOut>(Func<T, TOut> f) where TOut : class => o is { } v ? f(v) : null; } }
    public static class SS { extension<T>(T? o) where T : struct { public TOut? Map<TOut>(Func<T, TOut> f) where TOut : struct => o is { } v ? f(v) : null; } }
    public static class Use
    {
        public static string? M1(int? x) => x.Map(v => v.ToString());
        public static int? M2(int? x) => x.Map(v => v + 1);
        public static int? M3(string? s) => s.Map(v => v.Length);
        public static string? M4(string? s) => s.Map(v => v.ToUpperInvariant());
        // generic caller: does not compile without a constraint (unconstrained T? = T)
        // public static TOut? G<T, TOut>(T? x, Func<T, TOut> f) => x.Map(f);
    }
}
