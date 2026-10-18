using System.Text;

namespace OpenRevo.Core;

public static class DiagnosticFile
{
    public static void Write(string path, string json)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
