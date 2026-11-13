using System.Runtime.CompilerServices;
using System.Text.Json;
using Tarol.Results;
static string T(Func<string> f) { try { return f(); } catch (Exception e) { return "THROWS " + e.GetType().Name + ": " + e.Message; } }
Console.WriteLine($"size Result={Unsafe.SizeOf<Result>()} Result<int>={Unsafe.SizeOf<Result<int>>()} Result<Guid>={Unsafe.SizeOf<Result<Guid>>()} Result<string>={Unsafe.SizeOf<Result<string>>()} Violation={Unsafe.SizeOf<Violation>()}");
Result d = default; Result<int> dt = default;
Console.WriteLine($"R-1 default(Result): IsFailure={d.IsFailure} Code is null={d.Violation.Code is null} Type={d.Violation.Type}; default(Result<int>).IsFailure={dt.IsFailure}");
Console.WriteLine($"R-2 Violation.None.ToHttpStatusCode()={Violation.None.ToHttpStatusCode()}; Result r=Violation.None -> IsFailure={((Result)Violation.None).IsFailure}");
Result<string> ns = (string?)null!; Console.WriteLine($"R-3 Result<string> from null: IsSuccess={ns.IsSuccess} Value is null={ns.Value is null}");
Console.WriteLine($"R-4 serialize failure: {T(() => JsonSerializer.Serialize(Result<int>.Failure(Violation.NotFound("x"))))}");
Console.WriteLine($"R-4 roundtrip success: {T(() => JsonSerializer.Deserialize<Result<int>>(JsonSerializer.Serialize(Result<int>.Success(1))).IsSuccess.ToString())}");
var agg = Result.Combine(Result.Failure(Violation.NotFound("a")), Result.Failure(Violation.Forbidden()));
Console.WriteLine($"R-6 Combine(NotFound, Forbidden) -> Type={agg.Violation.Type} http={agg.Violation.ToHttpStatusCode()}");
var e1 = Violation.Aggregate("x", [Violation.NotFound("a")]); var e2 = Violation.Aggregate("x", [Violation.NotFound("a")]);
Console.WriteLine($"R-6 equal aggregates Equals={e1.Equals(e2)}");
var c2 = ResultExtensions.Combine(Result<int>.Failure(Violation.NotFound("a")), Result<int>.Failure(Violation.Conflict("b")));
Console.WriteLine($"R-7 Combine<T1,T2>(NotFound a, Conflict b) -> Code={c2.Violation.Code} InnerErrors={(c2.Violation.InnerErrors?.Count.ToString() ?? "null")}");
long a0 = GC.GetAllocatedBytesForCurrentThread(); var nf = Violation.NotFound<string>(Guid.NewGuid()); Console.WriteLine($"R-10 NotFound<T>(Guid) allocated {GC.GetAllocatedBytesForCurrentThread() - a0} B");
Console.WriteLine($"R-5 Result<object> = violation -> IsSuccess={R5.C(Violation.NotFound("x")).IsSuccess}; = (object)violation -> IsSuccess={R5.B(Violation.NotFound("x")).IsSuccess}");
