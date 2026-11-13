// The clock-isolation tests need two test classes to run at the same time; unlimited parallel threads make that
// independent of the machine's processor count (xUnit v3 defaults to one thread per core).
[assembly: Xunit.v3.Parallelization(MaxThreads = -1)]
