using Probe.Caching;

switch (args)
{
    case ["selftest", var kind]: return await SelfTest.Run(kind);
    case ["bench", .. var rest]: await Bench.Run(rest); return 0;
    default:
        Console.WriteLine("usage: selftest <postgres|redis|garnet|local|http> | bench <seed n|load s|find s|micro|list s|hits s|stale n|conflict s|http s|l2 <local|redis|garnet> s>");
        return 2;
}
