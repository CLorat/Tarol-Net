using Probe;
var f = Failure.NotFound("x");
C2Result<object> a = (object)f; Console.WriteLine($"Result<object> from (object)failure: is Failure={a is Failure}");
C2Result<object> b = new(f); Console.WriteLine($"Result<object> new(failure): is Failure={b is Failure}");
C2Result<Failure> c = new(f); Console.WriteLine($"Result<Failure> new(failure): IsSuccess={c.IsSuccess}");
BResult<object> d = f; Console.WriteLine($"union Result<object> = failure: switch -> {d switch { Failure => "Failure arm", object => "object arm" }}");
BResult<object> e = (object)f; Console.WriteLine($"union Result<object> = (object)failure: switch -> {e switch { Failure => "Failure arm", object => "object arm" }}");
