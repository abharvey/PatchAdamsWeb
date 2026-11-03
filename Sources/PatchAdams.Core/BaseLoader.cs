using System.Formats.Tar;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;

namespace PatchAdams.Core;

public sealed record BaseOrigin(string Kind, string Location);

public interface IFileProvider
{
    BaseOrigin Origin { get; }
    byte[]? ReadFile(string path);
    IReadOnlyList<string> GetPaths();
}

public sealed class BaseLoaderException(string message) : Exception(message);

public static class BaseLoader
{
    public static IFileProvider Open(string path, string? revision = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            if (revision is not null)
                return new GitRevisionFileProvider(fullPath, revision);
            return new DirectoryFileProvider(fullPath);
        }
        if (!File.Exists(fullPath))
            throw new BaseLoaderException($"{fullPath}: no such file or directory");
        if (revision is not null)
            throw new BaseLoaderException("A revision requires a Git repository directory.");

        return Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".zip" => ArchiveFileProvider.FromZip(fullPath),
            ".tar" => ArchiveFileProvider.FromTar(fullPath, gzip: false),
            ".tgz" => ArchiveFileProvider.FromTar(fullPath, gzip: true),
            _ when fullPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) =>
                ArchiveFileProvider.FromTar(fullPath, gzip: true),
            _ => throw new BaseLoaderException($"{Path.GetFileName(fullPath)}: unsupported archive type")
        };
    }

    internal static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new BaseLoaderException($"invalid base path: {path}");
        return path;
    }

    internal static bool IsSafeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized[0] != '/' &&
               !Path.IsPathRooted(normalized) &&
               !normalized.Split('/').Any(part => part is ".." or "." || part.Contains(':')) &&
               normalized.Split('/').All(part => part.Length > 0);
    }
}

public sealed class GitRevisionFileProvider : IFileProvider
{
    private static readonly HashSet<string> AllowedSubcommands =
        ["rev-parse", "ls-tree", "cat-file", "--version"];
    private readonly string repository;
    private readonly string commit;
    public string RequestedRevision { get; }
    public string Commit => commit;

    public GitRevisionFileProvider(string repository, string revision)
    {
        this.repository = Path.GetFullPath(repository);
        RequestedRevision = revision;
        if (!Directory.Exists(this.repository))
            throw new BaseLoaderException($"{this.repository}: no such repository directory");
        var resolved = Run(["rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}"], this.repository);
        commit = resolved.ExitCode == 0 ? DecodeStdout(resolved.StandardOutput).Trim() : "";
        if (commit.Length is not (40 or 64))
            throw new BaseLoaderException($"{revision}: not a commit in {this.repository} — {DecodeStdout(resolved.StandardError).Trim()}");
        Origin = new BaseOrigin("gitRevision", $"{Path.GetFileName(this.repository)}@{revision} ({commit[..12]})");
    }

    public BaseOrigin Origin { get; }

    public byte[]? ReadFile(string path)
    {
        path = BaseLoader.ValidateRelativePath(path);
        var result = Run(["cat-file", "blob", $"{commit}:{path}"], repository);
        if (result.ExitCode == 0)
            return result.StandardOutput;
        var error = DecodeStdout(result.StandardError);
        if (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("is a tree", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Not a valid object name", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase))
            return null;
        throw new BaseLoaderException($"git cat-file {path}: {error.Trim()}");
    }

    public IReadOnlyList<string> GetPaths()
    {
        var result = Run(["ls-tree", "-r", "-z", "--name-only", commit], repository);
        if (result.ExitCode != 0)
            throw new BaseLoaderException($"git ls-tree: {DecodeStdout(result.StandardError).Trim()}");
        return Encoding.UTF8.GetString(result.StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
    }

    public static string? GitVersion()
    {
        try
        {
            var result = Run(["--version"], null);
            return result.ExitCode == 0 ? DecodeStdout(result.StandardOutput).Trim() : null;
        }
        catch (BaseLoaderException)
        {
            return null;
        }
    }

    private static (int ExitCode, byte[] StandardOutput, byte[] StandardError) Run(
        IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        if (arguments.Count == 0 || !AllowedSubcommands.Contains(arguments[0]))
            throw new BaseLoaderException($"git subcommand not allowed: {arguments.FirstOrDefault() ?? ""}");
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        try
        {
            using var process = Process.Start(start) ??
                throw new BaseLoaderException("could not start git");
            process.StandardInput.Close();
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errorTask = process.StandardError.BaseStream.CopyToAsync(error);
            Task.WaitAll(outputTask, errorTask);
            process.WaitForExit();
            return (process.ExitCode, output.ToArray(), error.ToArray());
        }
        catch (Exception exception) when (exception is not BaseLoaderException)
        {
            throw new BaseLoaderException($"cannot run git: {exception.Message}");
        }
    }

    private static string DecodeStdout(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

public sealed class DirectoryFileProvider : IFileProvider
{
    private readonly string root;
    private readonly string rootPrefix;

    public DirectoryFileProvider(string root)
    {
        this.root = Path.GetFullPath(root);
        rootPrefix = this.root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Origin = new BaseOrigin("directory", this.root);
    }

    public BaseOrigin Origin { get; }

    public byte[]? ReadFile(string path)
    {
        path = BaseLoader.ValidateRelativePath(path);
        var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new BaseLoaderException($"invalid base path: {path}");
        RejectReparsePoints(fullPath);
        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    public IReadOnlyList<string> GetPaths()
    {
        var paths = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Contains(".git", StringComparer.Ordinal))
                continue;
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                paths.Add(relative);
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private void RejectReparsePoints(string path)
    {
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".")
                continue;
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new BaseLoaderException($"base path traverses a symbolic link: {path}");
        }
    }
}

public sealed class ArchiveFileProvider : IFileProvider
{
    private readonly IReadOnlyDictionary<string, byte[]> files;

    private ArchiveFileProvider(string path, IDictionary<string, byte[]> entries)
    {
        Origin = new BaseOrigin("archive", path);
        var safe = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (entryPath, data) in entries)
        {
            if (!BaseLoader.IsSafeArchivePath(entryPath))
                continue;
            var normalized = entryPath.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            safe[normalized] = data;
        }
        var prefixes = safe.Keys.Select(key => key.Split('/', 2)[0]).Distinct(StringComparer.Ordinal).ToArray();
        var prefix = prefixes.Length == 1 && safe.Keys.All(key => key.StartsWith(prefixes[0] + "/", StringComparison.Ordinal))
            ? prefixes[0] + "/"
            : "";
        files = safe.ToDictionary(
            entry => prefix.Length == 0 ? entry.Key : entry.Key[prefix.Length..],
            entry => entry.Value,
            StringComparer.Ordinal);
    }

    public BaseOrigin Origin { get; }

    public byte[]? ReadFile(string path)
    {
        path = BaseLoader.ValidateRelativePath(path);
        return files.TryGetValue(path, out var data) ? data : null;
    }

    public IReadOnlyList<string> GetPaths() => files.Keys.Order(StringComparer.Ordinal).ToArray();

    internal static ArchiveFileProvider FromZip(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || !BaseLoader.IsSafeArchivePath(entry.FullName))
                continue;
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            entries[entry.FullName] = output.ToArray();
        }
        return new ArchiveFileProvider(path, entries);
    }

    internal static ArchiveFileProvider FromTar(string path, bool gzip)
    {
        using var file = File.OpenRead(path);
        using Stream input = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TarReader(input);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile) ||
                entry.DataStream is null || !BaseLoader.IsSafeArchivePath(entry.Name))
                continue;
            using var output = new MemoryStream();
            entry.DataStream.CopyTo(output);
            entries[entry.Name] = output.ToArray();
        }
        return new ArchiveFileProvider(path, entries);
    }
}

public static class BaseText
{
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
