using System.Runtime.CompilerServices;
using O;

// per-operation union with the language keyword: exactly the outcomes this operation can have
var user = new User("Ada");
int s = 0; long a = 0;
for (int round = 0; round < 2; round++) { a = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1000; i++) { RenameOutcome r = user; s += r switch { User => 1, Invalid => 2, NotFound => 3 }; } }
Console.WriteLine($"union RenameOutcome(User, Invalid, NotFound): size={Unsafe.SizeOf<RenameOutcome>()} alloc/1000 success+switch={GC.GetAllocatedBytesForCurrentThread() - a} B");
a = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1000; i++) { CountOutcome r = i; s += r switch { int v => v, Invalid => 2 }; }
Console.WriteLine($"union CountOutcome(int, Invalid): alloc/1000 = {GC.GetAllocatedBytesForCurrentThread() - a} B");
RenameOutcome d = default;
try { Console.WriteLine(d switch { User => "u", Invalid => "i", NotFound => "nf" }); } catch (Exception e) { Console.WriteLine($"default RenameOutcome in switch: {e.GetType().Name}"); }

// composing: an operation calls another one with a different union
static RenameOutcome Rename(LoadOutcome loaded, string name) => loaded switch
{
    User u => u with { Name = name },
    NotFound nf => nf,           // each case re-wrapped by hand
};
Console.WriteLine($"compose: {Rename(new NotFound("User.NotFound"), "x").Value}");
static string Missing(RenameOutcome r) => r switch { User => "u", Invalid => "i" };   // L22
// generic framework code cannot name 'any union with a success case T':
static async ValueTask<T> Commit<T>(Func<ValueTask<T>> work) => await work();      // returns the union opaque: framework cannot see failure

namespace O
{
    public sealed record User(string Name);
    public sealed record Invalid(string Field, string Code);
    public sealed record NotFound(string Code);
    public union LoadOutcome(User, NotFound);
    public union RenameOutcome(User, Invalid, NotFound);
    public union CountOutcome(int, Invalid);
}
