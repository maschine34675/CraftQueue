using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Stores
{
    [Injectable(InjectionType = InjectionType.Singleton)]
    public class JsonStore<T>(ILogger<JsonStore<T>> logger)
    {
        private const string profileDataFilepathPrefix = "user/profileData/";
        private static readonly ConcurrentDictionary<string, object> fileLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public T? Load(MongoId profileId, string fileName)
        {
            string fullPath = getFullPath(profileId, fileName);
            lock (fileLocks.GetOrAdd(fullPath, _ => new object()))
            {
                if (!File.Exists(fullPath))
                    return tryLoadBackup(fullPath);

                try
                {
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(fullPath), jsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    string corruptPath = fullPath + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                    try
                    {
                        File.Move(fullPath, corruptPath, false);
                        logger.LogError(ex, "[CraftQueue] Queue data was corrupt and has been moved to {CorruptPath}.", corruptPath);
                    }
                    catch (Exception moveEx)
                    {
                        logger.LogError(moveEx, "[CraftQueue] Queue data was corrupt and could not be moved from {Path}.", fullPath);
                    }

                    T? backup = tryLoadBackup(fullPath);
                    if (backup != null)
                    {
                        logger.LogWarning("[CraftQueue] Recovered queue data from {BackupPath}.", fullPath + ".bak");
                        return backup;
                    }

                    logger.LogWarning("[CraftQueue] No usable queue backup exists; this profile will start with an empty queue.");
                    return default;
                }
            }
        }

        public void Save(T obj, MongoId profileId, string fileName)
        {
            string fullPath = getFullPath(profileId, fileName);
            lock (fileLocks.GetOrAdd(fullPath, _ => new object()))
            {
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string tempPath = fullPath + ".tmp";
                string backupPath = fullPath + ".bak";
                string json = JsonSerializer.Serialize(obj, jsonOptions);

                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }

                    if (File.Exists(fullPath))
                        File.Copy(fullPath, backupPath, true);
                    File.Move(tempPath, fullPath, true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); }
                        catch { }
                    }
                }
            }
        }

        private T? tryLoadBackup(string fullPath)
        {
            string backupPath = fullPath + ".bak";
            if (!File.Exists(backupPath))
                return default;

            try
            {
                T? value = JsonSerializer.Deserialize<T>(File.ReadAllText(backupPath), jsonOptions);
                if (value != null && !File.Exists(fullPath))
                {
                    try { File.Copy(backupPath, fullPath, true); }
                    catch (Exception restoreEx)
                    {
                        logger.LogError(restoreEx, "[CraftQueue] Queue backup could not be restored to {Path}.", fullPath);
                    }
                }
                return value;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "[CraftQueue] Queue backup {BackupPath} could not be read.", backupPath);
                return default;
            }
        }

        private static string getFullPath(MongoId profileId, string fileName)
        {
            string relativePath = Path.Combine(profileDataFilepathPrefix, profileId.ToString(), fileName);
            return Path.GetFullPath(relativePath, Directory.GetCurrentDirectory());
        }
    }
}
