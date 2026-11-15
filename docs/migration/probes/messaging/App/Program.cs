using ArtFlow;

var mode = args.Length > 0 ? args[0] : "selftest";
return mode switch
{
    "selftest" => await SelfTest.Run(args.Length > 1 ? args[1] : "memory"),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: selftest <memory|postgres|postgres+notify|nats|rabbit>");
    return 2;
}
