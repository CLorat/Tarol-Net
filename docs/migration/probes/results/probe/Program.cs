using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Probe;

static void P(string s) => Console.WriteLine(s);
// ---------------- 1. sizes
P("== sizes (Unsafe.SizeOf)");
P($"A  Result<int>={Unsafe.SizeOf<AResult<int>>()} <Guid>={Unsafe.SizeOf<AResult<Guid>>()} <Id<User>>={Unsafe.SizeOf<AResult<Id<User>>>()} <string>={Unsafe.SizeOf<AResult<string>>()} Result={Unsafe.SizeOf<AResult>()} Violation={Unsafe.SizeOf<Violation>()}");
P($"B  Result<int>={Unsafe.SizeOf<BResult<int>>()} <Guid>={Unsafe.SizeOf<BResult<Guid>>()} <Id<User>>={Unsafe.SizeOf<BResult<Id<User>>>()} <string>={Unsafe.SizeOf<BResult<string>>()} Result={Unsafe.SizeOf<BUnitResult>()}");
P($"C1 Result<int>={Unsafe.SizeOf<C1Result<int>>()} <Guid>={Unsafe.SizeOf<C1Result<Guid>>()} <Id<User>>={Unsafe.SizeOf<C1Result<Id<User>>>()} <string>={Unsafe.SizeOf<C1Result<string>>()}");
P($"C2 Result<int>={Unsafe.SizeOf<C2Result<int>>()} <Guid>={Unsafe.SizeOf<C2Result<Guid>>()} <Id<User>>={Unsafe.SizeOf<C2Result<Id<User>>>()} <string>={Unsafe.SizeOf<C2Result<string>>()} Result={Unsafe.SizeOf<C2Result>()}");
P($"D  Result<int>={Unsafe.SizeOf<DResult<int>>()} <Guid>={Unsafe.SizeOf<DResult<Guid>>()} <Id<User>>={Unsafe.SizeOf<DResult<Id<User>>>()} <string>={Unsafe.SizeOf<DResult<string>>()}");

// ---------------- 2. allocations, 1000 iterations each, after warm-up
const int N = 1000;
var fail = Failure.NotFound("User.NotFound"); var viol = new Violation { Code = "User.NotFound", Type = ViolationType.NotFound }; var prob = new NotFound("User.NotFound");
var id = new Id<User>(Guid.NewGuid());
long Measure(Func<int> body) { body(); long a = GC.GetAllocatedBytesForCurrentThread(); int acc = body(); long b = GC.GetAllocatedBytesForCurrentThread(); Sink = acc; return b - a; }

P("== allocated bytes per 1000 (create success + switch/Match)");
P($"A  success+Match(static)   {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { AResult<int> r = i; s += r.Match(static v => v, static _ => -1); } return s; })}");
P($"B  success+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { BResult<int> r = i; s += r switch { int v => v, Failure => -1 }; } return s; })}");
P($"B  Id<User> success+switch {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { BResult<Id<User>> r = id; s += r switch { Id<User> v => 1, Failure => -1 }; } return s; })}");
P($"B  failure+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { BResult<int> r = fail; s += r switch { int v => v, Failure => -1 }; } return s; })}");
P($"C1 success+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C1Result<int> r = new(i); s += r switch { int v => v, Failure => -1 }; } return s; })}");
P($"C2 success+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<int> r = i; s += r switch { int v => v, Failure => -1 }; } return s; })}");
P($"C2 Id<User> success+switch {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<Id<User>> r = id; s += r switch { Id<User> v => 1, Failure => -1 }; } return s; })}");
P($"C2 failure+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<int> r = fail; s += r switch { int v => v, Failure => -1 }; } return s; })}");
P($"C2 success+Match(static)   {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<int> r = i; s += r.Match(static v => v, static _ => -1); } return s; })}");
P($"C2 Match(capturing lambda) {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<int> r = i; int k = i; s += r.Match(v => v + k, _ => -1); } return s; })}");
P($"D  success+switch          {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { DResult<int> r = i; s += r switch { int v => v, NotFound => -1, Conflict => -2, Invalid => -3, Uninitialized => -4 }; } return s; })}");
P($"A  Map+Bind chain (static) {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { AResult<int> r = i; s += r.Map(static v => v + 1).Bind(static v => (AResult<long>)(long)v).Map(static v => (int)v).Match(static v => v, static _ => 0); } return s; })}");
P($"C2 Map+Bind chain (static) {Measure(() => { int s = 0; for (int i = 0; i < N; i++) { C2Result<int> r = i; s += r.Map(static v => v + 1).Bind(static v => (C2Result<long>)(long)v).Map(static v => (int)v).Match(static v => v, static _ => 0); } return s; })}");

// ---------------- 3. default
P("== default");
AResult<int> ad = default; P($"A  default: IsSuccess={ad.IsSuccess} Violation.Code is null={ad.Violation.Code is null} Type={ad.Violation.Type}");
#pragma warning disable CS8655 // measured: CS8655 is reported here
BResult<int> bd = default; P($"B  default: Value is null={bd.Value is null}; switch(no null arm) -> {Try(() => bd switch { int v => "ok", Failure => "fail" })}");
#pragma warning restore CS8655
C2Result<int> cd = default; P($"C2 default: IsSuccess={cd.IsSuccess}; switch(no null arm) -> {cd switch { int v => "ok " + v, Failure f => "fail " + f.Code }}");
DResult<int> dd = default; P($"D  default: switch -> {dd switch { int v => "ok", NotFound => "nf", Conflict => "c", Invalid => "i", Uninitialized u => "uninit " + u.Code }}");
C2Result<int> cdef = default; P($"C2 default is-pattern: (cdef is int)={cdef is int} (cdef is Failure)={cdef is Failure} (cdef is null)={cdef is null}");
C2Result<string> nul = new("x"); try { nul = new C2Result<string>((string)null!); } catch (ArgumentNullException) { P("C2 null success rejected: ArgumentNullException"); }

// ---------------- 4. JSON
P("== JSON");
var user = new User { Name = "Ada" };
#if !NOREFL
#pragma warning disable IL2026, IL3050
var ro = new JsonSerializerOptions();
P($"A  refl success: {Try(() => JsonSerializer.Serialize(AResult<int>.Success(1), ro))}");
P($"A  refl failure: {Try(() => JsonSerializer.Serialize(AResult<int>.Failure(viol), ro))}");
P($"A  refl roundtrip: {Try(() => JsonSerializer.Deserialize<AResult<int>>(JsonSerializer.Serialize(AResult<int>.Success(1), ro), ro).IsSuccess.ToString())}");
foreach (var (name, json) in new[] { ("B<int> ok", Try(() => JsonSerializer.Serialize((BResult<int>)5, ro))), ("B<int> fail", Try(() => JsonSerializer.Serialize((BResult<int>)fail, ro))), ("B<User> ok", Try(() => JsonSerializer.Serialize((BResult<User>)user, ro))), ("C2<int> ok", Try(() => JsonSerializer.Serialize((C2Result<int>)5, ro))), ("C2<int> fail", Try(() => JsonSerializer.Serialize((C2Result<int>)fail, ro))), ("C2<User> ok", Try(() => JsonSerializer.Serialize((C2Result<User>)user, ro))), ("C2<int> default", Try(() => JsonSerializer.Serialize(default(C2Result<int>), ro))) })
    P($"{name,-14} refl ser: {json}");
P($"B<int> refl deser 5: {Try(() => JsonSerializer.Deserialize<BResult<int>>("5", ro).Value?.ToString())}");
P($"B<int> refl deser fail: {Try(() => JsonSerializer.Deserialize<BResult<int>>("{\"Kind\":4,\"Code\":\"x\"}", ro).Value?.ToString())}");
P($"B<User> refl deser: {Try(() => JsonSerializer.Deserialize<BResult<User>>("{\"Name\":\"Ada\"}", ro).Value?.ToString())}");
P($"C2<int> refl deser 5: {Try(() => JsonSerializer.Deserialize<C2Result<int>>("5", ro).Value?.ToString())}");
P($"C2<int> refl deser fail: {Try(() => JsonSerializer.Deserialize<C2Result<int>>("{\"Kind\":4,\"Code\":\"x\"}", ro).Value?.ToString())}");
P($"C2<User> refl deser: {Try(() => JsonSerializer.Deserialize<C2Result<User>>("{\"Name\":\"Ada\"}", ro).Value?.ToString())}");
#pragma warning restore IL2026, IL3050
#endif
P($"C2<int> srcgen ser ok: {Try(() => JsonSerializer.Serialize((C2Result<int>)5, Ctx.Default.C2ResultInt32))}");
P($"C2<int> srcgen ser fail: {Try(() => JsonSerializer.Serialize((C2Result<int>)fail, Ctx.Default.C2ResultInt32))}");
P($"C2<int> srcgen deser 5: {Try(() => JsonSerializer.Deserialize("5", Ctx.Default.C2ResultInt32).Value?.ToString())}");
P($"C2<int> srcgen deser fail: {Try(() => JsonSerializer.Deserialize("{\"Kind\":4,\"Code\":\"x\"}", Ctx.Default.C2ResultInt32).Value?.ToString())}");
P($"B<int> srcgen ser ok: {Try(() => JsonSerializer.Serialize((BResult<int>)5, Ctx.Default.BResultInt32))}");
P($"B<int> srcgen deser 5: {Try(() => JsonSerializer.Deserialize("5", Ctx.Default.BResultInt32).Value?.ToString())}");
P($"B<int> srcgen deser fail: {Try(() => JsonSerializer.Deserialize("{\"Kind\":4,\"Code\":\"x\"}", Ctx.Default.BResultInt32).Value?.ToString())}");
P($"Failure srcgen: {Try(() => JsonSerializer.Serialize(fail, Ctx.Default.Failure))}");

P($"E<User> conv ser ok: {Try(() => JsonSerializer.Serialize((EResult<User>)user, Ctx2.Default.EResultUser))}");
P($"E<User> conv ser fail: {Try(() => JsonSerializer.Serialize((EResult<User>)fail, Ctx2.Default.EResultUser))}");
P($"E<int> conv ser default: {Try(() => JsonSerializer.Serialize(default(EResult<int>), Ctx2.Default.EResultInt32))}");
P($"E<User> conv deser ok: {Try(() => JsonSerializer.Deserialize("{\"value\":{\"Name\":\"Ada\"}}", Ctx2.Default.EResultUser).Value is User u ? u.Name : "?")}");
P($"E<User> conv deser fail: {Try(() => JsonSerializer.Deserialize("{\"failure\":{\"Kind\":5,\"Code\":\"c\"}}", Ctx2.Default.EResultUser).Value?.ToString())}");
P($"C2<string> srcgen ser ok: {Try(() => JsonSerializer.Serialize((C2Result<string>)"hi", Ctx2.Default.C2ResultString))}");
P($"C2<string> srcgen deser: {Try(() => JsonSerializer.Deserialize("\"hi\"", Ctx2.Default.C2ResultString).Value?.ToString())}");
P($"C2<int> default srcgen ser: {Try(() => JsonSerializer.Serialize(default(C2Result<int>), Ctx.Default.C2ResultInt32))}");
P($"D<int> srcgen ser fail: {Try(() => JsonSerializer.Serialize((DResult<int>)new NotFound("x"), Ctx2.Default.DResultInt32))}");
P($"D<int> srcgen deser fail: {Try(() => JsonSerializer.Deserialize(JsonSerializer.Serialize((DResult<int>)new NotFound("x"), Ctx2.Default.DResultInt32), Ctx2.Default.DResultInt32).Value?.ToString())}");
static string Try(Func<string?> f) { try { return f() ?? "<null>"; } catch (Exception e) { return $"THROWS {e.GetType().Name}: {e.Message.Split('\n')[0]}"; } }

[JsonSerializable(typeof(C2Result<int>))]
[JsonSerializable(typeof(BResult<int>))]
[JsonSerializable(typeof(Failure))]
partial class Ctx : JsonSerializerContext;
static partial class Program { public static int Sink; }
[JsonSerializable(typeof(EResult<User>))]
[JsonSerializable(typeof(EResult<int>))]
[JsonSerializable(typeof(C2Result<string>))]
[JsonSerializable(typeof(DResult<int>))]
partial class Ctx2 : JsonSerializerContext;
