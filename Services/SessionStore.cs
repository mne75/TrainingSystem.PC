using System.IO;
using System.Text.Json;
using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>
/// Lagring af sessioner og referenceruter (FR-05 og FR-06). Alt ligger lokalt på pc'en
/// som JSON. Ingen målinger forlader maskinen, og en gemt fil indeholder ingen
/// personhenførbare oplysninger.
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrainingSystem");

    public static string SessionsFolder => Path.Combine(RootFolder, "Sessions");
    public static string RoutesFolder => Path.Combine(RootFolder, "ReferenceRoutes");

    // ---------------- sessioner ----------------

    public static string SaveSession(SessionRecord session)
    {
        Directory.CreateDirectory(SessionsFolder);
        string path = Path.Combine(SessionsFolder, session.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(session, Options));
        return path;
    }

    public static SessionRecord LoadSession(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionRecord>(json)
               ?? throw new InvalidDataException("Filen indeholder ikke en gyldig session.");
    }

    // ---------------- referenceruter ----------------

    public static string SaveRoute(ReferenceRoute route)
    {
        Directory.CreateDirectory(RoutesFolder);
        string path = Path.Combine(RoutesFolder, SafeFileName(route.Name) + "_" + route.Id[..8] + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(route, Options));
        return path;
    }

    public static List<ReferenceRoute> LoadRoutes()
    {
        var routes = new List<ReferenceRoute>();
        if (!Directory.Exists(RoutesFolder)) return routes;

        foreach (string path in Directory.EnumerateFiles(RoutesFolder, "*.json"))
        {
            try
            {
                var route = JsonSerializer.Deserialize<ReferenceRoute>(File.ReadAllText(path));
                if (route is { IsUsable: true }) routes.Add(route);
            }
            catch (JsonException)
            {
                // En beskadiget rutefil må ikke forhindre de øvrige i at blive vist.
            }
        }

        routes.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return routes;
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length == 0 ? "rute" : name;
    }
}
