using System.Runtime.CompilerServices;
using System.Text.Json;
using Probe;
using Probe.B;
using Tarol.Results;

static void P(string s) => Console.WriteLine(s);
static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "THROWS " + e.GetType().Name + ": " + e.Message.Split('\n')[0]; } }

P($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, AOT={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
// ---------- 3. sizes
P("== sizes (Unsafe.SizeOf)");
P($"A  int? {Unsafe.SizeOf<int?>()}  Guid? {Unsafe.SizeOf<Guid?>()}  Id<User>? {Unsafe.SizeOf<Id<User>?>()}  string? {Unsafe.SizeOf<string>()}  User? {Unsafe.SizeOf<User>()}");
P($"C  Option<int> {Unsafe.SizeOf<Option<int>>()}  Option<Guid> {Unsafe.SizeOf<Option<Guid>>()}  Option<Id<User>> {Unsafe.SizeOf<Option<Id<User>>>()}  Option<string> {Unsafe.SizeOf<Option<string>>()}  Option<User> {Unsafe.SizeOf<Option<User>>()}");
P($"D  DOption<int> {Unsafe.SizeOf<DOption<int>>()}  DOption<Guid> {Unsafe.SizeOf<DOption<Guid>>()}  DOption<Id<User>> {Unsafe.SizeOf<DOption<Id<User>>>()}  DOption<string> {Unsafe.SizeOf<DOption<string>>()}  DOption<User> {Unsafe.SizeOf<DOption<User>>()}");
P($"E  Result<int> {Unsafe.SizeOf<Result<int>>()}  Result<Guid> {Unsafe.SizeOf<Result<Guid>>()}  Result<Id<User>> {Unsafe.SizeOf<Result<Id<User>>>()}  Result<string> {Unsafe.SizeOf<Result<string>>()}  Result<User> {Unsafe.SizeOf<Result<User>>()}");

// ---------- 3. allocations
P("== allocations, 1000 iterations after warm-up (bytes)");
const int N = 1000;
static long Measure(Func<int, int> body) { int s = 0; for (int i = 0; i < 100; i++) s += body(i); var b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < N; i++) s += body(i); var a = GC.GetAllocatedBytesForCurrentThread(); GC.KeepAlive(s); return a - b; }
var idv = new Id<User>(Guid.NewGuid()); var user = new User("Ada");
[MethodImpl(MethodImplOptions.NoInlining)] static int? AFindInt(int i) => i % 2 == 0 ? i : null;
[MethodImpl(MethodImplOptions.NoInlining)] static Option<int> CFindInt(int i) => i % 2 == 0 ? i : Option.None;
[MethodImpl(MethodImplOptions.NoInlining)] static DOption<int> DFindInt(int i) => i % 2 == 0 ? i : new None();
[MethodImpl(MethodImplOptions.NoInlining)] static Result<int> EFindInt(int i) => i % 2 == 0 ? i : Error.NotFound("x");
[MethodImpl(MethodImplOptions.NoInlining)] static Option<Id<User>> CFindId(int i, Id<User> id) => i % 2 == 0 ? id : Option.None;
[MethodImpl(MethodImplOptions.NoInlining)] static DOption<Id<User>> DFindId(int i, Id<User> id) => i % 2 == 0 ? id : new None();
P($"A  int? create+check       {Measure(i => AFindInt(i) is { } v ? v : 0)}");
P($"B  int? create+Or          {Measure(i => AFindInt(i).Or(0))}");
P($"B  int? create+Match(static) {Measure(i => AFindInt(i).Match(static v => v, static () => 0))}");
P($"C  Option<int> create+switch {Measure(i => CFindInt(i) switch { int v => v, None => 0 })}");
P($"C  Option<Id> create+switch  {Measure(i => CFindId(i, idv) switch { Id<User> v => 1, None => 0 })}");
P($"C  Option<int> Map->Bind->Filter->GetValueOrDefault (static) {Measure(i => CFindInt(i).Map(static v => v + 1).Bind(static v => v > 3 ? Option.Some(v) : Option.None).Filter(static v => v != 7).GetValueOrDefault(0))}");
P($"C  Option<int> Match(capturing) {Measure(i => CFindInt(i).Match(v => v + i, () => i))}");
P($"D  DOption<int> create+switch {Measure(i => DFindInt(i) switch { int v => v, None => 0 })}");
P($"D  DOption<Id> create+switch  {Measure(i => DFindId(i, idv) switch { Id<User> v => 1, None => 0 })}");
P($"E  Result<int> (NotFound cached? no: new Error each) create+switch {Measure(i => EFindInt(i) switch { int v => v, Violation => 0 })}");
var nf = Error.NotFound("User.NotFound");
P($"E  Result<int> with cached Error  {Measure(i => (i % 2 == 0 ? (Result<int>)i : nf) switch { int v => v, Violation => 0 })}");
P($"C->E  Option<int>.ToResult(cached) {Measure(i => CFindInt(i).ToResult(nf) switch { int v => v, Violation => 0 })}");
P($"E->C  Result<int>.ToOption         {Measure(i => ((Result<int>)i).ToOption() switch { int v => v, None => 0 })}");
P($"A->E  int?.ToResult(cached)        {Measure(i => AFindInt(i).ToResult(nf) switch { int v => v, Violation => 0 })}");

// ---------- 4. default and null
P("== default / null");
Option<int> c0 = default; Option<string> cs0 = default;
P($"C default(Option<int>): IsSome={c0.IsSome}, switch -> {(c0 switch { int v => "Some " + v, None => "None" })}; Value={c0.Value}");
P($"C default(Option<string>): switch -> {(cs0 switch { string v => "Some " + v, None => "None" })}");
P($"C new Option<string>(null!): {Try(() => new Option<string>(null!).ToString()!)}");
P($"C Option.Some<string>(null!): {Try(() => Option.Some<string>(null!).IsSome.ToString())}");
P($"C Option<string> o = (string)null!: {Try(() => { Option<string> o = (string)null!; return o.IsSome.ToString(); })}");
P($"C Option.FromNullable((string?)null) -> {(Option.FromNullable((string?)null) switch { string v => v, None => "None" })}; FromNullable((int?)null) -> {(Option.FromNullable((int?)null) switch { int v => v.ToString(), None => "None" })}");
#pragma warning disable CS8714, CS8655
Option<string?> cns = default; Option<int?> cni = (int?)5; Option<int?> cnn = (int?)null;
P($"C Option<string?> default -> {(cns switch { string v => v, None => "None" })}; Option<int?> (int?)5 -> {(cni switch { int v => "Some " + v, None => "None" })}; Option<int?> (int?)null: IsSome={cnn.IsSome} -> {Try(() => cnn switch { int v => "Some " + v, None => "None" })}");

P($"C `o is var x`: {Try(() => { Option<string> o = "a"; return o is var x ? x.GetType().Name : "-"; })}; `o is {{ }} x`: {Try(() => { Option<string> o = "a"; return o is { } x ? x.GetType().Name : "-"; })}; None `is {{ }}`: {Try(() => { Option<string> o = default; return (o is { }).ToString(); })}; None `is null`: {Try(() => { Option<string> o = default; return (o is null).ToString(); })}");
DOption<int> d0 = default; DOption<string> ds0 = default;
P($"D default(DOption<int>): Value is null={d0.Value is null}; switch -> {Try(() => d0 switch { int v => "Some " + v, None => "None" })}");
P($"D default(DOption<string>): switch -> {Try(() => ds0 switch { string v => v, None => "None" })}");
P($"D new DOption<string>((string)null!): Value is null={new DOption<string>((string)null!).Value is null}; switch -> {Try(() => new DOption<string>((string)null!) switch { string v => v, None => "None" })}");
P($"D DOption<int> 5: Value type={((object)new DOption<int>(5).Value!).GetType().Name} (boxed)");
Result<int> e0 = default;
#pragma warning restore CS8714, CS8655
P($"E default(Result<int>) -> {(e0 switch { int v => "Some", Violation v => v.ToString() })}");
P($"A generic Find<int>() via T? unconstrained -> {Gen.Find<int>()} (absence lost); Find<string>() -> {Gen.Find<string>() ?? "null"}");

// ---------- 5. JSON
P("== JSON reflection" );
#if !NOREFL
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
P($"A ADto some: {JsonSerializer.Serialize(new ADto(1, "ada", 36, Guid.Empty), web)}");
P($"A ADto none: {JsonSerializer.Serialize(new ADto(1, null, null, null), web)}");
P($"A read {{\"id\":1}} -> {JsonSerializer.Deserialize<ADto>("{\"id\":1}", web)}");
P($"C CDto some: {JsonSerializer.Serialize(new CDto(1, "ada", 36, Guid.Empty), web)}");
P($"C CDto none: {JsonSerializer.Serialize(new CDto(1, default, default, default), web)}");
P($"C read {{\"id\":1}} -> {Show(JsonSerializer.Deserialize<CDto>("{\"id\":1}", web)!)}");
P($"C read {{\"id\":1,\"nick\":null,\"age\":null}} -> {Show(JsonSerializer.Deserialize<CDto>("{\"id\":1,\"nick\":null,\"age\":null}", web)!)}");
P($"C read {{\"id\":1,\"nick\":\"x\",\"age\":3}} -> {Show(JsonSerializer.Deserialize<CDto>("{\"id\":1,\"nick\":\"x\",\"age\":3}", web)!)}");
P($"C CDtoIgnore none (WhenWritingDefault): {JsonSerializer.Serialize(new CDtoIgnore { Id = 1 }, web)}; some: {JsonSerializer.Serialize(new CDtoIgnore { Id = 1, Nick = "a", Age = 0 }, web)}");
var wn = new JsonSerializerOptions(web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
P($"C CDto none with DefaultIgnoreCondition=WhenWritingNull: {JsonSerializer.Serialize(new CDto(1, default, default, default), wn)}");
P($"C Option<User> some: {JsonSerializer.Serialize(Option.Some(user), web)} none: {JsonSerializer.Serialize(default(Option<User>), web)}");
var env = new JsonSerializerOptions(web); env.Converters.Add(new OptionEnvelopeConverter<string>()); env.Converters.Add(new OptionEnvelopeConverter<int>()); env.Converters.Add(new OptionEnvelopeConverter<Guid>());
P($"C envelope CDto some: {JsonSerializer.Serialize(new CDto(1, "ada", 36, Guid.Empty), env)}");
P($"C envelope CDto none: {JsonSerializer.Serialize(new CDto(1, default, default, default), env)}");
P($"C envelope read {{\"id\":1,\"nick\":{{}},\"age\":{{\"value\":0}}}} -> {Show(JsonSerializer.Deserialize<CDto>("{\"id\":1,\"nick\":{},\"age\":{\"value\":0}}", env)!)}");
P($"C Option<Option<int>> none-inner via null contract: {JsonSerializer.Serialize(Option.Some(default(Option<int>)), web)} -> reads back as {JsonSerializer.Deserialize<Option<Option<int>>>("null", web).IsSome} (nested none collapses)");
P($"D DDto some: {Try(() => JsonSerializer.Serialize(new DDto(1, "ada", 36), web))}");
P($"D DDto none: {Try(() => JsonSerializer.Serialize(new DDto(1, new None(), new None()), web))}");
P($"D DDto default: {Try(() => JsonSerializer.Serialize(new DDto(1, default, default), web))}");
P($"D read {{\"id\":1,\"nick\":\"x\",\"age\":{{}}}} -> {Try(() => { var d = JsonSerializer.Deserialize<DDto>("{\"id\":1,\"nick\":\"x\",\"age\":{}}", web)!; return $"nick={d.Nick.Value} age={d.Age.Value?.GetType().Name}"; })}");
P($"D read {{\"id\":1}} -> {Try(() => { var d = JsonSerializer.Deserialize<DDto>("{\"id\":1}", web)!; return $"nick.Value null={d.Nick.Value is null}"; })}");
P($"D DOption<User> some: {Try(() => JsonSerializer.Serialize(new DOption<User>(user), web))}; read back: {Try(() => JsonSerializer.Deserialize<DOption<User>>("{\"name\":\"Ada\"}", web).Value!.ToString()!)}");
P($"E Result<User> NotFound: {JsonSerializer.Serialize((Result<User>)Error.NotFound("User.NotFound"), web)}");
#else
P("(reflection disabled)");
#endif
P("== JSON source-generated (ProbeContext, camelCase)");
var ctx = ProbeContext.Default;
P($"A ADto none: {JsonSerializer.Serialize(new ADto(1, null, null, null), ctx.ADto)}");
P($"C CDto some: {JsonSerializer.Serialize(new CDto(1, "ada", 36, Guid.Empty), ctx.CDto)}");
P($"C CDto none: {JsonSerializer.Serialize(new CDto(1, default, default, default), ctx.CDto)}");
P($"C read {{\"id\":1}} -> {Show(JsonSerializer.Deserialize("{\"id\":1}", ctx.CDto)!)}");
P($"C read {{\"id\":1,\"nick\":null,\"age\":7,\"ref\":\"00000000-0000-0000-0000-000000000001\"}} -> {Show(JsonSerializer.Deserialize("{\"id\":1,\"nick\":null,\"age\":7,\"ref\":\"00000000-0000-0000-0000-000000000001\"}", ctx.CDto)!)}");
P($"C CDtoIgnore none: {JsonSerializer.Serialize(new CDtoIgnore { Id = 1 }, ctx.CDtoIgnore)}");
P($"C Option<User> some: {JsonSerializer.Serialize(Option.Some(user), ctx.OptionUser)}; Option<Id<User>> some: {Try(() => JsonSerializer.Serialize(Option.Some(idv), ctx.OptionIdUser))}");
P($"D DDto some: {Try(() => JsonSerializer.Serialize(new DDto(1, "ada", 36), ctx.DDto))}; none: {Try(() => JsonSerializer.Serialize(new DDto(1, new None(), new None()), ctx.DDto))}");
P($"D read {{\"id\":1,\"nick\":\"x\",\"age\":{{}}}} -> {Try(() => { var d = JsonSerializer.Deserialize("{\"id\":1,\"nick\":\"x\",\"age\":{}}", ctx.DDto)!; return $"nick={d.Nick.Value} age={d.Age.Value?.GetType().Name}"; })}");

P("== conditional with default (natural type T)");
P($"Option<int> F(bool b, int v) => b ? v : default;  F(false, 5) -> {(Trap.T1(false, 5) switch { int v => "Some " + v, None => "None" })}");
P($"Option<int> F(bool b, int v) => b ? v : Option.None;  F(false, 5) -> {(Trap.T2(false, 5) switch { int v => "Some " + v, None => "None" })}");
P($"Option<string> F(bool b, string v) => b ? v : default;  F(false, \"a\") -> {Try(() => Trap.T3(false, "a") switch { string v => "Some " + v, None => "None" })}");
P($"Result<int> F(bool b, int v) => b ? v : default;  F(false, 5) -> {(Trap.T4(false, 5) switch { int v => "Success " + v, Violation x => x.ToString() })}");
P($"Option<int> F(int? v) => v ?? default;  F(null) -> {(Trap.T5(null) switch { int v => "Some " + v, None => "None" })}");
#pragma warning restore CS8714, CS8655
static string Show(CDto d) => $"nick={(d.Nick switch { string s => s, None => "None" })} age={(d.Age switch { int a => a.ToString(), None => "None" })} ref={(d.Ref switch { Guid g => g.ToString(), None => "None" })}";

static class Gen { public static T? Find<T>() => default; }
