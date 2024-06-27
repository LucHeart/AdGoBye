using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdGoBye.Plugins;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace AdGoBye;

public class Indexer
{
    private static readonly ILogger Logger = Log.ForContext(typeof(Indexer));
    public static readonly string WorkingDirectory = GetWorkingDirectory();

    public static void ManageIndex()
    {
        using var db = new State.IndexContext();
        if (db.Content.Any()) VerifyDbTruth();
        const int maxRetries = 3;
        const int delayMilliseconds = 5000;

        DirectoryInfo[]? contentFolders = null;
        for (var retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                contentFolders = new DirectoryInfo(GetCacheDir()).GetDirectories();
                break;
            }
            catch (DirectoryNotFoundException ex)
            {
                // print exception with exception message if it's the last retry
                if (retry == maxRetries - 1)
                {
                    Logger.Fatal(ex, "Max retries reached. Unable to find your game's Cache directory, please define the folder above manually in appsettings.json as 'WorkingFolder'.");
                    Environment.Exit(1);
                }
                else
                {
                    Logger.Error($"Directory not found attempting retry: {retry + 1} of {maxRetries}");
                }
                Thread.Sleep(delayMilliseconds); 
            }
        }
        
        if (contentFolders.Length == db.Content.Count() - SafeAllowlistCount()) return;

        var content = contentFolders
            .ExceptBy(db.Content.Select(content => content.StableContentName), info => info.Name)
            .Select(newContent => GetLatestFileVersion(newContent).HighestVersionDirectory)
            .Where(directory => directory != null);

        if (content != null)
        {
            AddToIndex(content);
        }

        Logger.Information("Finished Index processing");
        return;

        static int SafeAllowlistCount()
        {
            return Settings.Options.Allowlist is not null ? Settings.Options.Allowlist.Length : 0;
        }
    }

    private static void VerifyDbTruth()
    {
        using var db = new State.IndexContext();
        foreach (var content in db.Content.Include(content => content.VersionMeta))
        {
            var directoryMeta = new DirectoryInfo(content.VersionMeta.Path);
            if (!directoryMeta.Parent!.Exists) // This content doesn't have a StableContentId folder anymore
            {
                db.Remove(content);
                continue;
            }

            // We know the content is still being tracked but we don't know if its actually relevant
            // so we'll resolve every version to determine the highest and mutate based on that
            var (highestVersionDir, highestVersion) = GetLatestFileVersion(directoryMeta.Parent);
            if (highestVersionDir is null)
            {
                Logger.Warning(
                    "{parentDir} ({id}) is lingering with no content versions and should be deleted, Skipping for now",
                    directoryMeta.Parent, content.Id);
                continue;
            }


            if (!File.Exists(highestVersionDir.FullName + "/__data"))
            {
                db.Remove(content);
                Logger.Warning(
                    "{directory} is highest version but doesn't have __data, hell might have frozen over. Removed from Index",
                    highestVersionDir.FullName);
                continue;
            }

            if (highestVersion <= content.VersionMeta.Version) continue;
            content.VersionMeta.Version = highestVersion;
            content.VersionMeta.Path = highestVersionDir.FullName;
            content.VersionMeta.PatchedBy = [];
        }

        db.SaveChangesSafe();
    }

    public static void AddToIndex(string path)
    {
        if (AddToIndexPart1(path, out var content) && content != null)
        {
            AddToIndexPart2(content);
        }
    }

    public static void AddToIndex(IEnumerable<DirectoryInfo?> paths)
    {
        ConcurrentBag<Content> contents = [];
        Parallel.ForEach(paths, path =>
        {
            if (path != null && AddToIndexPart1(path.FullName, out var content) && content != null)
            {
                contents.Add(content);
            }
        });

        var groupedById = contents.GroupBy(content => content.Id);

        Parallel.ForEach(groupedById, group =>
        {
            foreach (var content in group)
            {
                AddToIndexPart2(content);
            }
        });
    }

    public static bool AddToIndexPart1(string path, out Content? content)
    {
        using var db = new State.IndexContext();
        //   - Folder (StableContentName) [singleton, we want this]
        //       - Folder (version) [may exist multiple times] 
        //          - __info
        //          - __data 
        //          - __lock (if currently used)
        var directory = new DirectoryInfo(path);
        if (directory.Name == "__data") directory = directory.Parent;

        // If we already have an item in Index that has the same StableContentName, then this is a newer version of something Indexed
        content = db.Content.Include(content => content.VersionMeta)
            .FirstOrDefault(content => content.StableContentName == directory!.Parent!.Name);
        if (content is not null)
        {
            var version = GetVersion(directory!.Name);
            if (version < content.VersionMeta.Version)
            {
                Logger.Verbose(
                    "Skipped Indexation of {directory} since it isn't an upgrade (Index: {greaterVersion}, Parsed: {lesserVersion})",
                    directory.FullName, content.VersionMeta.Version, version);
                return false;
            }

            content.VersionMeta.Version = version;
            content.VersionMeta.Path = directory.FullName;
            content.VersionMeta.PatchedBy = [];

            db.SaveChangesSafe();
            return false;
        }

        if (!File.Exists(directory!.FullName + "/__data")) return false;
        content = FileToContent(directory);
        return true;
    }

    private static void AddToIndexPart2(Content content)
    {
        using var db = new State.IndexContext();
        var indexCopy = db.Content.Include(existingFile => existingFile.VersionMeta)
            .FirstOrDefault(existingFile => existingFile.Id == content.Id);

        if (indexCopy is null)
        {
            if (content.Type is ContentType.Avatar && IsAvatarImposter(content.VersionMeta.Path))
            {
                Logger.Information("Skipping {path} because it's an Imposter avatar", content.VersionMeta.Path);
                return;
            }

            db.Content.Add(content);
            Logger.Information("Added {id} [{type}] to Index", content.Id, content.Type);
            db.SaveChangesSafe();
            return;
        }

        switch (indexCopy.Type)
        {
            // There are two unique cases where content may share a Content ID with a different StableContentName
            // The first is a world Unity version upgrade, in that case we simply use the newer version
            case ContentType.World:
                if (!IsWorldHigherUnityVersion(indexCopy, content)) return;
                Logger.Information("Upgrading {id} since it bumped Unity version ({directory})",
                    indexCopy.Id, content.VersionMeta.Path);
                indexCopy.VersionMeta.Version = content.VersionMeta.Version;
                indexCopy.VersionMeta.Path = content.VersionMeta.Path;
                indexCopy.VersionMeta.PatchedBy = [];
                db.SaveChangesSafe();
                return;
            // The second is an Imposter avatar, which we don't want to index.
            case ContentType.Avatar:
                if (IsAvatarImposter(content.VersionMeta.Path))
                {
                    Logger.Information("Skipping {path} because it's an Imposter avatar", content.VersionMeta.Path);
                    return;
                }

                break;
            default:
                throw new NotImplementedException();
        }

        return;

        static bool IsAvatarImposter(string path)
        {
            AssetsManager manager = new();
            var bundleInstance = manager.LoadBundleFile(path + "/__data");
            var assetInstance = manager.LoadAssetsFileFromBundle(bundleInstance, 0);

            foreach (var monoScript in assetInstance.file.GetAssetsOfType(AssetClassID.MonoScript))
            {
                var monoScriptBase = manager.GetBaseField(assetInstance, monoScript);
                if (monoScriptBase["m_ClassName"].IsDummy ||
                    monoScriptBase["m_ClassName"].AsString != "Impostor") continue;
                return true;
            }

            return false;
        }

        static bool IsWorldHigherUnityVersion(Content indexedContent, Content newContent)
        {
            // Hack: [Regalia 2023-12-25T03:33:18Z] I'm not paid enough to parse Unity version strings reliably
            // This assumes the Unity versions always contains the major version at the start, is seperated by a dot and 
            // the major versions will always be greater to each other. Upcoming Unity 6 will violate this assumption
            // but I'm betting that service provider won't upgrade anytime soon
            var indexedContentVersion = ResolveUnityVersion(indexedContent.VersionMeta.Path).Split(".")[0];
            var newContentVersion = ResolveUnityVersion(newContent.VersionMeta.Path).Split(".")[0];
            return int.Parse(newContentVersion) > int.Parse(indexedContentVersion);
        }

        static string ResolveUnityVersion(string path)
        {
            AssetsManager manager = new();
            var bundleInstance = manager.LoadBundleFile(path + "/__data");
            var assetInstance = manager.LoadAssetsFileFromBundle(bundleInstance, 1);

            foreach (var monoScript in assetInstance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var monoScriptBase = manager.GetBaseField(assetInstance, monoScript);
                if (monoScriptBase["unityVersion"].IsDummy) continue;
                return monoScriptBase["unityVersion"].AsString;
            }

            Logger.Fatal("ResolveUnityVersion: Unable to parse unityVersion out for {path}", path);
            throw new InvalidOperationException();
        }
    }


    public static Content? GetFromIndex(string path)
    {
        using var db = new State.IndexContext();
        var directory = new DirectoryInfo(path);
        return db.Content.Include(content => content.VersionMeta)
            .FirstOrDefault(content => content.StableContentName == directory.Parent!.Parent!.Name);
    }

    public static void RemoveFromIndex(string path)
    {
        using var db = new State.IndexContext();
        var indexMatch = GetFromIndex(path);
        if (indexMatch is null) return;


        db.Content.Remove(indexMatch);
        db.SaveChangesSafe();
        Logger.Information("Removed {id} from Index", indexMatch.Id);
    }

    public static void PatchContent(Content content)
    {
        if (content.Type is not ContentType.World) return;
        Logger.Information("Processing {ID} ({directory})", content.Id, content.VersionMeta.Path);

        var file = Path.Combine(content.VersionMeta.Path, "__data");
        var container = new ContentAssetManagerContainer(file);

        var pluginOverridesBlocklist = false;
        foreach (var plugin in PluginLoader.LoadedPlugins)
        {
            try
            {
                if (plugin.Instance.WantsIndexerTracking() &&
                    content.VersionMeta.PatchedBy.Contains(plugin.Name)) continue;

                var pluginApplies = plugin.Instance.PluginType() is EPluginType.Global;
                if (!pluginApplies && plugin.Instance.PluginType() is EPluginType.ContentSpecific)
                {
                    var ctIds = plugin.Instance.ResponsibleForContentIds();
                    if (ctIds is not null) pluginApplies = ctIds.Contains(content.Id);
                }

                pluginOverridesBlocklist = plugin.Instance.OverrideBlocklist(content);

                plugin.Instance.Initialize(content);

                if (plugin.Instance.Verify(content, ref container) is not EVerifyResult.Success)
                    pluginApplies = false;

                if (pluginApplies) plugin.Instance.Patch(content, ref container);

                if (!Settings.Options.DryRun && plugin.Instance.WantsIndexerTracking())
                    content.VersionMeta.PatchedBy.Add(plugin.Name);

                plugin.Instance.PostPatch(content);
            }
            catch (Exception e)
            {
                Logger.Error(e,
                    "Plugin {Name} ({Maintainer}) v{Version} threw an exception while patching {ID} ({path})",
                    plugin.Name, plugin.Maintainer, plugin.Version, content.Id, content.VersionMeta.Path);
            }
        }

        if (Blocklist.Blocks is not null && !pluginOverridesBlocklist &&
            !content.VersionMeta.PatchedBy.Contains("Blocklist"))
        {
            foreach (var block in Blocklist.Blocks.Where(block => block.Key.Equals(content.Id)))
            {
                try
                {
                    var unmatchedObjects = Blocklist.Patch(container, block.Value.ToArray());
                    if (Settings.Options.SendUnmatchedObjectsToDevs && unmatchedObjects is not null)
                        Blocklist.SendUnpatchedObjects(content, unmatchedObjects);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to patch {ID} ({path})", content.Id, content.VersionMeta.Path);
                }
            }
        }

        if (Settings.Options.DryRun) return;

        Logger.Information("Done, writing changes as bundle");

        container.Bundle.file.BlockAndDirInfo.DirectoryInfos[1].SetNewData(container.AssetsFile.file);
        using var writer = new AssetsFileWriter(file + ".clean");

        if (Settings.Options.EnableRecompression)
        {
            using var uncompressedMs = new MemoryStream();
            using var uncompressedWriter = new AssetsFileWriter(uncompressedMs);

            container.Bundle.file.Write(uncompressedWriter);

            var newUncompressedBundle = new AssetBundleFile();
            using var uncompressedReader = new AssetsFileReader(uncompressedMs);
            newUncompressedBundle.Read(uncompressedReader);

            newUncompressedBundle.Pack(writer, AssetBundleCompressionType.LZ4);

            newUncompressedBundle.Close();
            uncompressedWriter.Close();
            uncompressedMs.Close();
            uncompressedReader.Close();
        }
        else
        {
            container.Bundle.file.Write(writer);
        }

        // Moving the file without closing our access fails on NT.
        writer.Close();
        container.Bundle.file.Close();
        container.AssetsFile.file.Close();

        File.Replace(file + ".clean", file, Settings.Options.DisableBackupFile ? null : file + ".bak");

        if (!Settings.Options.DryRun) content.VersionMeta.PatchedBy.Add("Blocklist");
        Logger.Information("Processed {ID}", content.Id);
    }

    private static (DirectoryInfo? HighestVersionDirectory, int HighestVersion) GetLatestFileVersion(
        DirectoryInfo stableNameFolder)
    {
        var highestVersion = 0;
        DirectoryInfo? highestVersionDir = null;
        foreach (var directory in stableNameFolder.GetDirectories())
        {
            var version = GetVersion(directory.Name);
            if (version < highestVersion) continue;
            highestVersion = version;
            highestVersionDir = directory;
        }

        return (highestVersionDir, highestVersion);
    }

    private static Content? FileToContent(DirectoryInfo pathToFile)
    {
        string id;
        int type;
        try
        {
            (id, type) = ParseFileMeta(pathToFile.FullName)!;
        }
        catch (NullReferenceException) // null is a parsing issue from ParseFileMeta's side
        {
            return null;
        }

        return new Content
        {
            Id = id,
            Type = (ContentType)type,
            VersionMeta = new Content.ContentVersionMeta
            {
                Version = GetVersion(pathToFile.Name),
                Path = pathToFile.FullName,
                PatchedBy = []
            },
            StableContentName = pathToFile.Parent!.Name
        };
    }

    public static Tuple<string, int>? ParseFileMeta(string path)
    {
        AssetsManager manager = new();
        BundleFileInstance bundleInstance;

        try
        {
            bundleInstance = manager.LoadBundleFile(path + "/__data");
        }
        catch (NotImplementedException e)
        {
            if (e.Message != "Cannot handle bundles with multiple block sizes yet.") throw;
            Logger.Warning(
                "{directory} has multiple block sizes, AssetsTools can't handle this yet. Skipping... ",
                path);
            return null;
        }


        var bundle = bundleInstance!.file;

        var index = 0;
        AssetsFileInstance? assetInstance = null;
        foreach (var bundleDirectoryInfo in bundle.BlockAndDirInfo.DirectoryInfos)
        {
            if (bundleDirectoryInfo.Name.EndsWith(".sharedAssets"))
            {
                index++;
                continue;
            }

            assetInstance = manager.LoadAssetsFileFromBundle(bundleInstance, index);
        }

        if (assetInstance is null)
        {
            Logger.Warning(
                "Indexing {directory} caused no loadable bundle directory to exist, is this bundle valid?",
                path);
            return null;
        }

        var assetFile = assetInstance.file;

        foreach (var gameObjectBase in assetFile.GetAssetsOfType(AssetClassID.MonoBehaviour)
                     .Select(gameObjectInfo => manager.GetBaseField(assetInstance, gameObjectInfo)).Where(
                         gameObjectBase =>
                             !gameObjectBase["blueprintId"].IsDummy && !gameObjectBase["contentType"].IsDummy))
        {
            if (gameObjectBase["blueprintId"].AsString == "")
            {
                Logger.Warning("{directory} has no embedded ID for some reason, skipping this…", path);
                return null;
            }

            if (gameObjectBase["contentType"].AsInt >= 3)
            {
                Logger.Warning(
                    "{directory} is neither Avatar nor World but another secret other thing ({type}), skipping this...",
                    path, gameObjectBase["contentType"].AsInt);
                return null;
            }

            return new Tuple<string, int>(gameObjectBase["blueprintId"].AsString,
                gameObjectBase["contentType"].AsInt);
        }

        return null;
    }

    private static int GetVersion(string hexVersion)
    {
        var hex = hexVersion.TrimStart('0');
        if (hex.Length % 2 != 0) hex = '0' + hex;
        var bytes = Convert.FromHexString(hex);
        while (bytes.Length < 4)
        {
            var newValues = new byte[bytes.Length + 1];
            newValues[0] = 0x00;
            Array.Copy(bytes, 0, newValues, 1, bytes.Length);
            bytes = newValues;
        }

        return BitConverter.ToInt32(bytes);
    }


    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private static string GetWorkingDirectory()
    {
        if (!string.IsNullOrEmpty(Settings.Options.WorkingFolder)) return Settings.Options.WorkingFolder;
        var appName = SteamParser.GetApplicationName();
        var pathToWorkingDir = $"{appName}/{appName}/";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return ConstructLinuxWorkingPath();

        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            .Replace("Roaming", "LocalLow");
        return $"{appDataFolder}/{pathToWorkingDir}";

        [SupportedOSPlatform("linux")]
        string ConstructLinuxWorkingPath()
        {
            var protonWorkingPath =
                $"/steamapps/compatdata/{SteamParser.Appid}/pfx/drive_c/users/steamuser/AppData/LocalLow/{pathToWorkingDir}";

            if (string.IsNullOrEmpty(SteamParser.AlternativeLibraryPath))
                return SteamParser.GetPathToSteamRoot() + protonWorkingPath;

            return SteamParser.AlternativeLibraryPath + protonWorkingPath;
        }
    }


    private static string GetCacheDir()
    {
        return WorkingDirectory + "/Cache-WindowsPlayer/";
    }
}
