﻿using AdGoBye.PluginInternal;
using AdGoBye.Plugins;
using AssetsTools.NET;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGoBye
{
    internal class Patcher(Blocklist blocklists, IOptions<Settings.PatcherOptions> options, ILogger<Patcher> logger)
    {
        private readonly Settings.PatcherOptions _options = options.Value;

        internal void PatchContent(Content content)
        {
            if (content.Type is not ContentType.World) return;
            logger.LogInformation("Processing {ID} ({directory})", content.Id, content.VersionMeta.Path);

            var file = Path.Combine(content.VersionMeta.Path, "__data");
            var container = new ContentAssetManagerContainer(file);

            var estimatedUncompressedSize = EstimateDecompressedSize(container.Bundle.file);

            if (estimatedUncompressedSize > _options.ZipBombSizeLimitMB * 1000L * 1000L)
            {
                logger.LogWarning("Skipped {ID} ({directory}) because it's likely a ZIP Bomb ({estimatedMB}MB uncompressed).",
                    content.Id, content.VersionMeta.Path, (estimatedUncompressedSize / 1000 / 1000));
                return;
            }

            var pluginOverridesBlocklist = false;
            var someoneModifiedBundle = false;
            var pluginsDidPatch = new List<PluginEntry>();
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

                    if (pluginApplies)
                    {
                        var patchResult = plugin.Instance.Patch(content, ref container);
                        if (patchResult == EPatchResult.Success)
                        {
                            someoneModifiedBundle = true;
                            pluginsDidPatch.Add(plugin);
                        }
                    }

                    if (!_options.DryRun && plugin.Instance.WantsIndexerTracking())
                        content.VersionMeta.PatchedBy.Add(plugin.Name);

                    plugin.Instance.PostPatch(content);
                }
                catch (Exception e)
                {
                    logger.LogError(e,
                        "Plugin {Name} ({Maintainer}) v{Version} threw an exception while patching {ID} ({path})",
                        plugin.Name, plugin.Maintainer, plugin.Version, content.Id, content.VersionMeta.Path);
                }
            }

            if (blocklists.Blocks.Count != 0 && !pluginOverridesBlocklist &&
                !content.VersionMeta.PatchedBy.Contains("Blocklist"))
            {
                foreach (var block in blocklists.Blocks.Where(block => block.Key.Equals(content.Id)))
                {
                    try
                    {
                        if (blocklists.Patch(content, container, [.. block.Value]))
                            someoneModifiedBundle = true;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Failed to patch {ID} ({path})", content.Id, content.VersionMeta.Path);
                    }
                }
            }

            if (_options.DryRun) return;
            if (!someoneModifiedBundle) return;

            logger.LogInformation("Done, writing changes as bundle");

            container.Bundle.file.BlockAndDirInfo.DirectoryInfos[1].SetNewData(container.AssetsFile.file);
            using var writer = new AssetsFileWriter(file + ".clean");

            if (_options.EnableRecompression)
            {
                if (estimatedUncompressedSize > _options.RecompressionMemoryMaxMB * 1000L * 1000L
                    || estimatedUncompressedSize >=
                    1_900_000_000) // 1.9GB hard limit to leave a 100MB buffer just in case the estimation is off.
                {
                    var tempFileName = file + ".uncompressed";
                    using var uncompressedFs = File.Open(tempFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None);
                    CompressAndWrite(uncompressedFs);
                    File.Delete(tempFileName);
                }
                else
                {
                    using var uncompressedMs = new MemoryStream();
                    CompressAndWrite(uncompressedMs);
                }

                void CompressAndWrite(Stream stream)
                {
                    var newUncompressedBundle = new AssetBundleFile();
                    using var uncompressedWriter = new AssetsFileWriter(stream);

                    container.Bundle.file.Write(uncompressedWriter);

                    using var uncompressedReader = new AssetsFileReader(stream);
                    newUncompressedBundle.Read(uncompressedReader);

                    newUncompressedBundle.Pack(writer, AssetBundleCompressionType.LZ4);

                    newUncompressedBundle.Close();
                    uncompressedWriter.Close();
                    uncompressedReader.Close();
                    stream.Close();
                }
            }
            else
            {
                container.Bundle.file.Write(writer);
            }

            // Moving the file without closing our access fails on NT.
            writer.Close();
            container.Bundle.file.Close();
            container.AssetsFile.file.Close();

            File.Replace(file + ".clean", file, _options.DisableBackupFile ? null : file + ".bak");

            if (!_options.DryRun) content.VersionMeta.PatchedBy.Add("Blocklist");
            foreach (var plugin in pluginsDidPatch)
            {
                try
                {
                    plugin.Instance.PostDiskWrite(content);
                }
                catch (Exception e)
                {
                    logger.LogError(e,
                        "Plugin {Name} ({Maintainer}) v{Version} threw an exception while handling post disk write {ID} ({path})",
                        plugin.Name, plugin.Maintainer, plugin.Version, content.Id, content.VersionMeta.Path);
                }
            }
            logger.LogInformation("Processed {ID}", content.Id);
        }

        private static long EstimateDecompressedSize(AssetBundleFile assetBundleFile)
        {
            return assetBundleFile.BlockAndDirInfo.DirectoryInfos.Sum(x => x.DecompressedSize);
        }
    }
}