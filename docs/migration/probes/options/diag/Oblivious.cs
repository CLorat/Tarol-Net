#nullable disable
using Probe;
using Tarol.Results;
public static class Oblivious
{
    static string FindStr() => null; static int? FindInt() => null;
    static Option<string> OStr() => default; static Option<int> OInt() => default;
    public static int N1() => FindStr().Length;                         // N1 A in oblivious context
    public static int N2() => FindStr() switch { string v => v.Length };  // N2
    public static int N3() => FindInt().Value;                          // N3
    public static int N4() => OStr() switch { string v => v.Length };   // N4 C in oblivious context
    public static void N5() { string s = OStr(); }                      // N5
    public static Option<string> N6() => FindStr();                      // N6 null into Option, oblivious
}
