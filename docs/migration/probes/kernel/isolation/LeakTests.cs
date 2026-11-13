// Does an ambient clock set (and never disposed) in one test leak into the next test of the same class (run sequentially)?
public class H_Leak
{
    [Fact] public void T1_SetsWithoutDispose() { AmbientClock.SetProvider(Fixed.Clock()); Console.WriteLine($"RESULT leak T1 sets: {new Event().AmbientStamp.Year}"); }
    [Fact] public void T2_Reads() => Console.WriteLine($"RESULT leak T2 reads: {new Event().AmbientStamp.Year}");
    [Fact] public void T3_Reads() => Console.WriteLine($"RESULT leak T3 reads: {new Event().AmbientStamp.Year}");
}
