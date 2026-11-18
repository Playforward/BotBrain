using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using BotBrain.Maps;

// Builds a map package from a level's collision mesh.
//   build <mesh.soup> <points.json> <out dir> [--cell 0.5] [--radius 130] [--id name] [--no-navmesh] [--objectives file]
// With a navigation mesh (the default, package version 3) it also writes <id>.floors.png: every
// floor bots can walk, upstairs included; places with more than one floor are tinted violet;
// drop links are orange. --objectives (a sandbox objectives file) reports whether each objective
// is on a floor and how far each team walks to it.
// Writes <out dir>/<id>-<hash>.bbmap and <out dir>/<id>.png (a top-down picture
// of the walkable ground, lighter is higher; spawn points in red and blue,
// interest points in yellow).

if (args.Length < 4 || args[0] != "build")
{
    Console.Error.WriteLine("usage: build <mesh.soup> <points.json> <out dir> [--cell 0.5] [--id name]");
    return 2;
}
string soup = args[1], pointsPath = args[2], outDir = args[3];
MeshMapOptions options = new();
string? id = null, objectivesPath = null;
bool navmesh = true;
for (int i = 4; i < args.Length; i++)
{
    if (args[i] == "--no-navmesh")
        navmesh = false;
    else if (args[i] == "--objectives")
        objectivesPath = args[++i];
    else
    if (args[i] == "--cell")
        options.CellSize = float.Parse(args[++i]);
    else if (args[i] == "--radius")
        options.PlayRadius = float.Parse(args[++i]);
    else if (args[i] == "--id")
        id = args[++i];
}

TriangleMesh mesh = TriangleMesh.Load(soup);
using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(pointsPath));
id ??= doc.RootElement.GetProperty("level").GetString() ?? Path.GetFileNameWithoutExtension(soup);
List<MapSeed> seeds = [];
List<MapPoint> named = [];
foreach (JsonElement p in doc.RootElement.GetProperty("points").EnumerateArray())
{
    if (p.GetProperty("kind").GetString() != "spawn")
    {
        named.Add(new MapPoint(MapPointKind.Interest, 255, 0, new Vector2(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle()), 6f));
        continue;
    }
    seeds.Add(new MapSeed(
        new Vector3(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle()),
        (byte)p.GetProperty("team").GetInt32(), p.GetProperty("id").GetInt32()));
}

Console.WriteLine($"mesh: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, from {mesh.Min} to {mesh.Max}");
GridMap map = MeshMapBuilder.Build(id, mesh, seeds, options, out MeshMapReport report, named);
Console.WriteLine($"points: {map.Points.Count(p => p.Kind == MapPointKind.Spawn)} spawn, {map.Points.Count(p => p.Kind == MapPointKind.Interest)} interest ({named.Count} named in the level)");
Console.WriteLine($"seeds: {report.SeedsOnGround} of {report.Seeds} stand on walkable ground");
Console.WriteLine(report.InterestOnRoutes
    ? $"interest points: spread over {report.InterestArea:F0} m2 on the routes between the two sides"
    : $"interest points: spread over all {report.InterestArea:F0} m2 of walkable ground (no routes between two sides found)");
Console.WriteLine(report.Vantages > 0
    ? $"vantage points: {report.Vantages} (cyan), seeing {report.VantageBestShare:P0} down to {report.VantageWorstShare:P0} of the route sample"
    : "vantage points: none");
Console.WriteLine($"grid: {report.Width} x {report.Height} cells of {options.CellSize} m, {report.Cells} walkable "
    + $"({report.Cells * options.CellSize * options.CellSize:F0} m2), ground from {report.MinGround:F1} to {report.MaxGround:F1} m, built in {report.Seconds:F1} s");

Console.WriteLine($"one-way drops: {report.Drops}");
Console.WriteLine($"refused steps: {report.NoSurface} nothing to stand on, {report.TooSteep} too high or low, "
    + string.Join(", ", options.ProbeHeights.Select((h, i) => $"{report.Blocked[i]} blocked at {h} m")));
Console.WriteLine("  height differences refused (m: count): " + string.Join("  ", report.StepHistogram.Select(kv => $"{kv.Key * 0.25:F2}: {kv.Value}")));

if (navmesh)
{
    NavMap nav = BotBrain.MapTool.NavMeshBuild.Build(mesh, BotBrain.MapTool.NavMeshBuild.Default, out double navSeconds);
    (int tiles, int polygons, int drops) = nav.Count();
    Console.WriteLine($"navigation mesh: {polygons} polygons in {tiles} tiles, {drops} one-way drops, built in {navSeconds:F1} s");
    // Only floors someone can get to from a spawn count; roofs and sealed rooms are switched off.
    int kept = nav.KeepReachable(seeds.Select(s => s.Position));
    Console.WriteLine($"  {kept} of {polygons} polygons can be walked to from a spawn");
    // Roam points and camp spots found again on the mesh, every floor counting (M11 stage 4).
    MeshMapReport meshReport = new();
    List<MapPoint> meshPoints = BotBrain.MapTool.NavPoints.Find(map, nav, mesh, seeds, new MeshMapOptions(), meshReport);
    map = map.WithNav(nav, meshPoints);
    Console.WriteLine(meshReport.InterestOnRoutes
        ? $"  on the mesh: roam points spread over {meshReport.InterestArea:F0} m2 on the routes between the two sides"
        : $"  on the mesh: roam points spread over all {meshReport.InterestArea:F0} m2 (no routes between two sides found)");
    List<MapPoint> camps = [.. map.PointsOf(MapPointKind.Vantage)];
    Console.WriteLine(meshReport.Vantages > 0
        ? $"  on the mesh: {meshReport.Vantages} camp spots, seeing {meshReport.VantageBestShare:P0} down to {meshReport.VantageWorstShare:P0} of the route sample; "
            + $"{camps.Count(c => c.Height > map.GroundAt(c.Position) + 2f)} of them above the grid's ground (upper floors, roofs)"
        : "  on the mesh: no camp spots");
    List<MapSeed> firstOfTeam = [.. seeds.GroupBy(s => s.Team).Select(g => g.First())];
    List<MapPoint> roam = [.. map.PointsOf(MapPointKind.Interest).Where(p => p.Id < 1000)];
    foreach (MapSeed from in firstOfTeam)
    {
        int reached = roam.Count(p => float.IsFinite(nav.WalkLength(from.Position, map.PointAt(p))));
        Console.WriteLine($"  team {from.Team}'s spawn reaches {reached} of the {roam.Count} roam points");
    }
    if (objectivesPath is not null)
    {
        using JsonDocument objectives = JsonDocument.Parse(File.ReadAllText(objectivesPath));
        foreach (JsonElement o in objectives.RootElement.GetProperty("objectives").EnumerateArray())
        {
            Vector3 at = new(o.GetProperty("x").GetSingle(), o.GetProperty("y").GetSingle(), o.GetProperty("z").GetSingle());
            string name = o.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
            if (!nav.Floor(at, out _, out Vector3 floor, across: 2.5f, below: 2.6f, above: 0.2f))
            {
                Console.WriteLine($"  objective {name}: no floor within reach of it");
                continue;
            }
            Console.WriteLine($"  objective {name}: floor {Vector2.Distance(new(at.X, at.Y), new(floor.X, floor.Y)):F1} m across, {at.Z - floor.Z:F1} m below; "
                + string.Join(", ", firstOfTeam.Select(s => $"team {s.Team} walks {Walk(nav.WalkLength(s.Position, floor))}")));
        }
    }
}

DirectoryMapStore store = new(outDir);
string path = store.Save(map);
Console.WriteLine($"package: {path} ({new FileInfo(path).Length / 1024 / 1024.0:F1} MB)");

string picture = Path.Combine(outDir, id + ".png");
WritePicture(map, report, picture);
Console.WriteLine($"picture: {picture}");
if (map.Nav is { } floorsNav)
{
    string floors = Path.Combine(outDir, id + ".floors.png");
    WriteFloors(map, floorsNav, report, floors);
    Console.WriteLine($"floors picture: {floors}");
}
return 0;

static string Walk(float metres) => float.IsFinite(metres) ? $"{metres:F0} m" : "NO WAY";

// Every floor of the navigation mesh from above, framed like the grid picture: the top floor
// coloured by height, places with a second floor 2 m or more apart tinted violet, drops orange,
// and the points (roam yellow, camp spots cyan, a white ring round those above the ground).
static void WriteFloors(GridMap map, NavMap nav, MeshMapReport report, string path)
{
    int w = map.Width, h = map.Height;
    (float[] top, float[] low, List<Vector3> drops) = nav.FloorsFromAbove(map.Origin, map.CellSize, w, h);
    float range = MathF.Max(0.01f, report.MaxGround - report.MinGround);
    byte[] rgb = new byte[w * h * 3];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = ((h - 1 - y) * w + x) * 3, i = y * w + x;
            if (float.IsNaN(top[i]))
            {
                rgb[o] = rgb[o + 1] = rgb[o + 2] = 18;
                continue;
            }
            float t = Math.Clamp((top[i] - report.MinGround) / range, 0, 1);
            bool floors = top[i] - low[i] >= 2f;
            rgb[o] = (byte)(floors ? 150 + 80 * t : 60 + 150 * t);
            rgb[o + 1] = (byte)(floors ? 70 + 60 * t : 110 + 120 * t);
            rgb[o + 2] = (byte)(floors ? 200 + 50 * t : 70 + 90 * t);
        }
    void Dot(int cx, int cy, int radius, (byte, byte, byte) colour, bool ring = false)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx, y = cy + dy, d = dx * dx + dy * dy;
                if (x < 0 || y < 0 || x >= w || y >= h || d > radius * radius + 1 || (ring && d < (radius - 1) * (radius - 1)))
                    continue;
                int o = ((h - 1 - y) * w + x) * 3;
                (rgb[o], rgb[o + 1], rgb[o + 2]) = colour;
            }
    }
    foreach (Vector3 d in drops)
        Dot(map.CellX(d.X), map.CellY(d.Y), 0, ((byte)255, (byte)150, (byte)30));
    foreach (MapPoint p in map.Points)
    {
        (byte, byte, byte) colour = p.Kind == MapPointKind.Interest ? ((byte)255, (byte)220, (byte)0)
            : p.Kind == MapPointKind.Vantage ? ((byte)0, (byte)230, (byte)230)
            : p.Team == 0 ? ((byte)255, (byte)40, (byte)40) : ((byte)60, (byte)120, (byte)255);
        int cx = map.CellX(p.Position.X), cy = map.CellY(p.Position.Y);
        Dot(cx, cy, 3, colour);
        if (p.Kind != MapPointKind.Spawn && p.Height > map.GroundAt(p.Position) + 2f)
            Dot(cx, cy, 5, ((byte)255, (byte)255, (byte)255), ring: true);
    }
    WritePng(path, rgb, w, h);
}

static void WritePicture(GridMap map, MeshMapReport report, string path)
{
    int w = map.Width, h = map.Height;
    byte[] rgb = new byte[w * h * 3];
    float range = MathF.Max(0.01f, report.MaxGround - report.MinGround);
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            // Picture rows run top to bottom; map y runs upward.
            int o = ((h - 1 - y) * w + x) * 3;
            if (map.IsBlockedCell(x, y))
            {
                rgb[o] = rgb[o + 1] = rgb[o + 2] = 18;
                continue;
            }
            float t = (map.GroundAt(map.CellCenter(x, y)) - report.MinGround) / range;
            // Cells with a neighbour they cannot reach are edges: draw them darker.
            bool edge = false;
            for (int d = 0; d < 4 && !edge; d++)
                edge = !map.CanStep(x, y, d);
            float shade = edge ? 0.55f : 1f;
            rgb[o] = (byte)((60 + 150 * t) * shade);
            rgb[o + 1] = (byte)((110 + 120 * t) * shade);
            rgb[o + 2] = (byte)((70 + 90 * t) * shade);
        }
    }
    foreach (MapPoint p in map.Points)
    {
        (byte r, byte g, byte b) = p.Kind == MapPointKind.Interest ? ((byte)255, (byte)220, (byte)0)
            : p.Kind == MapPointKind.Vantage ? ((byte)0, (byte)230, (byte)230)
            : p.Team == 0 ? ((byte)255, (byte)40, (byte)40) : ((byte)60, (byte)120, (byte)255);
        int cx = map.CellX(p.Position.X), cy = map.CellY(p.Position.Y);
        for (int dy = -3; dy <= 3; dy++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || dx * dx + dy * dy > 10)
                    continue;
                int o = ((h - 1 - y) * w + x) * 3;
                rgb[o] = r;
                rgb[o + 1] = g;
                rgb[o + 2] = b;
            }
        }
    }
    WritePng(path, rgb, w, h);
}

static void WritePng(string path, byte[] rgb, int width, int height)
{
    using FileStream file = File.Create(path);
    file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    byte[] header = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8;
    header[9] = 2;
    Chunk(file, "IHDR", header);
    using MemoryStream packed = new();
    using (ZLibStream z = new(packed, CompressionLevel.Optimal, leaveOpen: true))
    {
        for (int y = 0; y < height; y++)
        {
            z.WriteByte(0);
            z.Write(rgb, y * width * 3, width * 3);
        }
    }
    Chunk(file, "IDAT", packed.ToArray());
    Chunk(file, "IEND", []);
}

static void Chunk(Stream stream, string type, byte[] data)
{
    Span<byte> length = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
    stream.Write(length);
    byte[] body = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
    stream.Write(body);
    uint crc = 0xFFFFFFFF;
    foreach (byte b in body)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }
    Span<byte> check = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(check, ~crc);
    stream.Write(check);
}
