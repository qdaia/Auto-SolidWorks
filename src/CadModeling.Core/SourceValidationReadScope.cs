using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CadModeling.Ir;

namespace CadModeling.Core;

internal sealed class SourceValidationReadScope : IDisposable
{
    [ThreadStatic]
    private static SourceValidationReadScope? current;

    private const int MaximumOpenFiles = 512;

    private readonly Dictionary<string, (FileStream Stream, string Hash)> files = new Dictionary<string, (FileStream, string)>(StringComparer.Ordinal);

    private readonly Dictionary<string, DrawingOmissionInventory> inventories = new Dictionary<string, DrawingOmissionInventory>(StringComparer.Ordinal);

    private bool disposed;

    internal static SourceValidationReadScope? Begin()
    {
        if (current != null || !OperatingSystem.IsWindows())
        {
            return null;
        }
        return current = new SourceValidationReadScope();
    }

    internal static string HashFile(string path)
    {
        var scope = current;
        if (scope == null)
        {
            return UncachedHash(path);
        }
        string fullPath = Path.GetFullPath(path);
        if (scope.files.TryGetValue(fullPath, out var value))
        {
            return value.Hash;
        }
        if (scope.files.Count >= MaximumOpenFiles || fullPath.StartsWith("\\\\", StringComparison.Ordinal) || HasReparsePoint(fullPath))
        {
            return UncachedHash(path);
        }
        FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string text = Convert.ToHexString(SHA256.HashData(fileStream));
            scope.files.Add(fullPath, (fileStream, text));
            return text;
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    internal static DrawingOmissionInventory? FindInventory(string path, string? hash)
    {
        if (current == null || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        return current.inventories.GetValueOrDefault(Path.GetFullPath(path) + "|" + hash);
    }

    internal static void PinInventory(string path)
    {
        if (current != null)
        {
            HashFile(path);
        }
    }

    internal static void RememberInventory(string path, string? hash, DrawingOmissionInventory inventory)
    {
        var scope = current;
        if (scope is null || scope.inventories.Count >= 8)
            return;

        IEnumerable<string?> artifacts = new[] { path, inventory.SourcePath };
        artifacts = artifacts.Concat(inventory.Pages.Where(page => page.Ingested).Select(page => page.ObservationPath));
        artifacts = artifacts.Concat(inventory.Regions.SelectMany(region =>
            new[] { region.ImagePath, region.InkMaskPath }.OfType<string>()));
        if (artifacts.All(artifact => artifact is not null && scope.files.ContainsKey(Path.GetFullPath(artifact))))
            scope.inventories[Path.GetFullPath(path) + "|" + hash] = inventory;
    }

    private static bool HasReparsePoint(string path)
    {
        for (string? text = path; text != null; text = Path.GetDirectoryName(text))
        {
            if ((File.GetAttributes(text) & FileAttributes.ReparsePoint) != FileAttributes.None)
            {
                return true;
            }
        }
        return false;
    }

    private static string UncachedHash(string path)
    {
        using FileStream source = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        current = null;
        foreach (var value in files.Values)
        {
            value.Stream.Dispose();
        }
        files.Clear();
        inventories.Clear();
    }
}
