using Probe;
using Probe.B;
using Tarol.Results;
#pragma warning disable CS8321
public static class Diag
{
    static void TakeInt(int x) { } static void TakeGuid(Guid x) { } static void TakeId(Id<User> x) { } static void TakeStr(string x) { } static void TakeUser(User x) { }
    static int? FindInt() => null; static Guid? FindGuid() => null; static Id<User>? FindId() => null; static string? FindStr() => null; static User? FindUser() => null;

    // ---------- A: plain T?  (1) read without check
    public static int A1i() => FindInt().Value;                 // A1i
    public static Guid A1g() => FindGuid().Value;              // A1g
    public static Guid A1d() => FindId().Value.Value;          // A1d
    public static int A1s() => FindStr().Length;               // A1s
    public static string A1u() => FindUser().Name;             // A1u
    // (2) switch without the null arm
    public static int A2i() => FindInt() switch { int v => v };       // A2i
    public static int A2s() => FindStr() switch { string v => v.Length }; // A2s
    public static string A2u() => FindUser() switch { User u => u.Name }; // A2u
    // (3) pass maybe-absent where present is required
    public static void A3i() => TakeInt(FindInt());            // A3i
    public static void A3d() => TakeId(FindId());              // A3d
    public static void A3s() => TakeStr(FindStr());            // A3s
    public static void A3u() => TakeUser(FindUser());          // A3u
    // (4) assign to non-nullable
    public static void A4s() { string s = FindStr(); }  // A4s
    public static void A4u() { User u = FindUser(); }   // A4u
    // (5) generic code: T? on unconstrained T loses absence for structs
    static T? FindT<T>() => default;
    public static int A5() => FindT<int>();                    // A5  (no diagnostic, returns 0)
    public static int A5b<T>(T? x) => x is null ? 0 : 1;      // A5b (for T=int always 1)
    // (6) suppression operator
    public static int A6() => FindStr()!.Length;               // A6 (silent)
    // (7) if (x.HasValue) style checks with .Value after check
    public static int A7(int? x) => x.HasValue ? x.Value : 0;  // A7 (ok)
    // (8) struct field of reference type default
    struct Holder { public string Name; }
    public static int A8() => default(Holder).Name.Length;     // A8 (silent NRE)
    // (9) array of non-null
    public static int A9() => (new string[3])[0].Length;         // A9 (silent NRE)

    // ---------- B: fixed extensions
    public static int B1i() => FindInt().IsSome ? FindInt()!.Value : 0;
    public static int B2i(int? x) => x.IsSome ? x.Value : 0;  // does [NotNullWhen] on a Nullable receiver silence CS8629? B2i
    public static int B2s(string? x) => x.IsSome ? x.Length : 0; // B2s
    public static int B3i(int? x) => x.Or(5);                  // B3i returns int
    public static string B3s(string? x) => x.Or("d");          // B3s returns string
    public static string B4i(int? x) => x.Match(v => v.ToString(), () => "none");
    public static int B4s(string? x) => x.Match(v => v.Length, () => 0);
    public static string? B5i(int? x) => x.MapClass(v => v.ToString()); // different name per kind
    public static int? B5s(string? x) => x.MapStruct(v => v.Length);
    public static Result<int> B6i(int? x) => x.ToResult(Error.NotFound("x"));
    public static Result<string> B6s(string? x) => x.ToResult(Error.NotFound("x"));

    // ---------- C: [Union] Option<T>
    static Option<int> OInt() => default; static Option<Guid> OGuid() => default; static Option<Id<User>> OId() => default; static Option<string> OStr() => default; static Option<User> OUser() => default;
    // (1) read without check
    public static int C1i() { int v = OInt(); return v; }      // C1i
    public static int C1s() => OStr().Length;                  // C1s
    public static object C1v() => OInt().Value;                // C1v (object, compiles)
    public static int C1t() { OInt().TryGetValue(out int v); return v; }  // C1t ignores bool: silent
    public static int C1t2() { OStr().TryGetValue(out string? v); return v.Length; } // C1t2 CS8602?
    // (2) switch without None arm
    public static int C2i() => OInt() switch { int v => v };   // C2i
    public static int C2s() => OStr() switch { string v => v.Length }; // C2s
    public static string C2u() => OUser() switch { User u => u.Name }; // C2u
    public static string C2d() => OId() switch { Id<User> u => u.ToString() }; // C2d
    public static int C2ok(Option<int> o) => o switch { int v => v, None => 0 }; // C2ok
    public static int C2null(Option<int> o) => o switch { int v => v, None => 0, null => -1 }; // C2null
    public static int C2stmt(Option<int> o) { switch (o) { case int v: return v; } return -1; } // C2stmt
    public static int C2is(Option<string> o) => o is string s ? s.Length : 0;  // C2is
    public static int C2var(Option<string> o) => o is var s ? 1 : 0;  // C2var
    public static int C2notnull(Option<string> o) => o is { } s ? 1 : 0;  // C2notnull: what is s?
    // (3) pass maybe-absent where present is required
    public static void C3i() => TakeInt(OInt());               // C3i
    public static void C3s() => TakeStr(OStr());               // C3s
    public static void C3u() => TakeUser(OUser());             // C3u
    // (4) creation
    public static Option<int> C4a() => 5;                      // C4a implicit
    public static Option<string> C4b() => FindStr();            // C4b maybe-null into Option: warning?
    public static Option<string> C4c() => Option.None;          // C4c
    public static Option<string> C4d() => default;              // C4d
    public static Option<string?> C4e() => default;             // C4e notnull violated
    public static Option<int?> C4f() => default;                // C4f notnull violated
    public static Option<User>? C4g() => default;               // C4g Nullable<Option<User>>
    public static Option<None> C4h() => default;                // C4h Option<None>
    public static Option<string> C4i(string? s) => new Option<string>(s);  // C4i
    public static Option<string> C4j() => Option.Some((string?)null);  // C4j

    // ---------- D: language union
    static DOption<int> DInt() => default; static DOption<string> DStr() => default;
    public static int D2i() => DInt() switch { int v => v };   // D2i
    public static int D2ok() => DInt() switch { int v => v, None => 0 };  // D2ok (default -> throws?)
    public static int D2s() => DStr() switch { string v => v.Length, None => 0 }; // D2s
    public static void D3i() => TakeInt(DInt());               // D3i

    // ---------- E: Result<T> with Error.NotFound
    static Result<int> RInt() => Error.NotFound("x"); static Result<User> RUser() => Error.NotFound("x");
    public static int E1() { int v = RInt(); return v; }       // E1
    public static int E2i() => RInt() switch { int v => v };   // E2i
    public static string E2u() => RUser() switch { User u => u.Name }; // E2u
    public static int E2ok() => RInt() switch { int v => v, Violation => 0 }; // E2ok
    public static void E3() => TakeUser(RUser());              // E3
    public static void E5() => RUser();                        // E5 discarded -> TRES002
}
