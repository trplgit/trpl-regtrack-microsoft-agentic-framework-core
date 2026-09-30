using DrainCheck;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DrainCheck <version>");
    Console.Error.WriteLine("Reads the hub connection string from ConnectionStrings__DurableTaskHub.");
    return 2;
}

var version = args[0];
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
    ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running DrainCheck.");

var count = await DrainCheckQuery.CountInFlightAsync(connectionString, version);

Console.WriteLine($"Version {version}: {count} in-flight instance(s) (Pending or Running).");

return count == 0 ? 0 : 1;
