using ArtFlow;

var mode = args.Length > 0 ? args[0] : "selftest";
return mode switch
{
    "selftest" => await SelfTest.Run(args.Length > 1 ? args[1] : "memory"),
    "security" => await Security.Run(args[1]),
    "api" or "worker" or "billing" => await Roles.Run(mode),
    "startup" => await Startup.Run(args[1]),
    "legacy" => await ArtFlow.Legacy.LegacyProbe.Run(),
    "bench" => await Bench.Run(args[1].Split(','), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4])),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: selftest <memory|postgres|postgres+notify|nats|rabbit>");
    return 2;
}
