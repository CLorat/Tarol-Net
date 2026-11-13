using Tarol.Options;
int? x = null;
var or = x.Or(5);                 Console.WriteLine($"O-2 int? x=null; x.Or(5): static type {Type(or)}, value {(or is null ? "null" : or.ToString())}");
var some = int.Some(42);          Console.WriteLine($"O-2 int.Some(42): static type {Type(some)}");
var m = x.Match(v => Type(v), () => "none"); int? y = 3; Console.WriteLine($"O-2 int? y=3; y.Match(v => typeof v): {y.Match(v => Type(v), () => "none")}");
try { var _ = x!.Value; } catch (Exception e) { Console.WriteLine($"O-3 int? null .Value: {e.GetType().Name}: {e.Message}"); }
string? s = null;
try { var _ = s.Value; } catch (Exception e) { Console.WriteLine($"O-3 string? null .Value (extension): {e.GetType().Name}: {e.Message}"); }
Console.WriteLine($"O-4 members on every type: 42.IsSome={42.IsSome}, \"a\".Or(\"b\")={"a".Or("b")}, DateTime.Now.IsNone={DateTime.Now.IsNone}");
static string Type<T>(T _) => typeof(T).Name == "Nullable`1" ? "Nullable<" + typeof(T).GetGenericArguments()[0].Name + ">" : typeof(T).Name;
