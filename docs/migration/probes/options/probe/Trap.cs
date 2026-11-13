using Probe;
using Tarol.Results;
public static class Trap
{
    public static Option<int> T1(bool b, int v) => b ? v : default;        // T1 natural type int -> Some(0)
    public static Option<int> T2(bool b, int v) => b ? v : Option.None;    // T2 target-typed -> None
#pragma warning disable CS8604 // deliberate: shows the run-time result
    public static Option<string> T3(bool b, string v) => b ? v : default;  // T3 natural type string -> default! = null -> throws
#pragma warning restore CS8604
    public static Result<int> T4(bool b, int v) => b ? v : default;        // T4 the same trap for Result: Success(0)
    public static Option<int> T5(int? v) => v ?? default;                   // T5 int -> Some(0)
}
