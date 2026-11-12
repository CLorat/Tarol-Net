using Probe;
public static class Diag
{
    // L1 C2 missing Failure arm
    public static int L1(C2Result<int> r) => r switch { int v => v };
    // L2 C2 complete, no null arm, parameter
    public static int L2(C2Result<int> r) => r switch { int v => v, Failure => 0 };
    // L3 B complete, no null arm, parameter
    public static int L3(BResult<int> r) => r switch { int v => v, Failure => 0 };
    // L4 B local default, no null arm
    public static int L4() { BResult<int> r = default; return r switch { int v => v, Failure => 0 }; }
    // L5 D complete over closed leaves
    public static int L5(DResult<int> r) => r switch { int v => v, NotFound => 1, Conflict => 2, Invalid => 3, Uninitialized => 4 };
    // L6 D one leaf missing
    public static int L6(DResult<int> r) => r switch { int v => v, NotFound => 1, Conflict => 2, Invalid => 3 };
    // L7 C2Result<Failure>: constructor
    public static object L7(Failure f) => new C2Result<Failure>(f);
    // L8 C2Result<Failure>: implicit
    // L8 removed (CS0457)
    // L9 C2Result<object>: implicit from object
    public static C2Result<object> L9(object o) => o;
    // L10 C2Result<object>: failure via ctor picks?
    public static C2Result<object> L10(Failure f) => new(f);
    // L11 discarded result
    public static void L11() { M(); }
    static C2Result<int> M() => 1;
    // L12 D: switch with base Problem arm
    public static int L12(DResult<int> r) => r switch { int v => v, Problem p => 1 };
    // L13 C2: subsumed / unreachable arm
    public static int L13(C2Result<int> r) => r switch { int v => v, Failure => 0, null => -1 };
    // L14 statement switch, missing arm (no exhaustiveness)
    public static int L14(C2Result<int> r) { switch (r) { case int v: return v; } return -1; }
    // L15 is-pattern narrowing
    public static int L15(C2Result<string> r) => r is string s ? s.Length : 0;
    // L16 B: Result<object> union case collision
    public static BResult<object> L16(Failure f) => f;
    // L17 C2 switch, value type case with property pattern
    public static string L17(C2Result<int> r) => r switch { int v and > 0 => "pos", int => "nonpos", Failure { Kind: FailureKind.NotFound } => "nf", Failure f => f.Code };
}
