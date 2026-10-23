namespace MyNotes.Services;

public static class ResourcePaths
{
    public static bool IsSegment(string value) => value.Length > 0 && value.Length <= 160 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') &&
        value is not "." and not ".." && !value.EndsWith('.') &&
        !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string root, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length == 0 || parts.Any(part => !IsSegment(part)))
            throw new InvalidDataException("resource paths must contain safe relative segments");
        var fullRoot = Path.GetFullPath(root);
        for (var ancestor = new DirectoryInfo(fullRoot); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.LinkTarget != null)
                throw new InvalidDataException("resource directories must be regular directories");
        var current = fullRoot;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("resource paths must use regular files and directories");
        }
        return current;
    }
}
