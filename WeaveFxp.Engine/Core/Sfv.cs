using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

public static class Sfv
{
    // Parses an .sfv body: "<filename> <crc32>" per line, ';' comments ignored.
    public static List<SfvFile> Parse(string raw)
    {
        var files = new List<SfvFile>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var idx = line.LastIndexOf(' ');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var crc = line[(idx + 1)..].Trim();
            if (name.Length == 0 || crc.Length == 0) continue;
            files.Add(new SfvFile { Name = name, Crc = crc });
        }
        return files;
    }
}
