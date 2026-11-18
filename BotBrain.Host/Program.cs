using System.Net;
using BotBrain.Core;
using BotBrain.Maps;

// The brain as a standalone process. Bodies connect to it over TCP.
//   --port N        port to listen on (default 24800)
//   --maps DIR      folder of map packages (default ./maps)
//   --all-full      run every agent at full fidelity
//   --record DIR    record every match into this folder for the replay viewer

int port = 24800;
string mapDirectory = "maps";
BrainOptions options = new();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i]);
            break;
        case "--maps":
            mapDirectory = args[++i];
            break;
        case "--all-full":
            options.ForceFullFidelity = true;
            break;
        case "--record":
            options.RecordDirectory = args[++i];
            break;
        case "--seed":
            options.Seed = ulong.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

using BrainServer server = new(new DirectoryMapStore(mapDirectory), options);
server.Log += Console.WriteLine;
server.WorldClosed += world => Console.WriteLine(world.Stats.Report(world.AgentCount, 30));
int bound = server.Start(IPAddress.Loopback, port);
Console.WriteLine($"brain listening on 127.0.0.1:{bound}, maps from {Path.GetFullPath(mapDirectory)}");
Thread.Sleep(Timeout.Infinite);
return 0;
