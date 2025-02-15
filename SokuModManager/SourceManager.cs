﻿using Newtonsoft.Json;
using SokuModManager.Models.Source;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SokuModManager
{
    public enum SourceManagerStatus
    {
        Pending,
        Fetching
    }

    public class SourceManagerStatusChangedEventArgs : EventArgs
    {
        public SourceManagerStatus Status { get; set; }
        public string Target { get; set; }
        public int? Progress { get; set; }
    }

    public class SourceManager
    {

        public event EventHandler<SourceManagerStatusChangedEventArgs> SourceManagerStatusChanged;
        private void OnSourceManagerStatusChanged(SourceManagerStatusChangedEventArgs e)
        {
            SourceManagerStatusChanged?.Invoke(this, e);
        }

        public List<SourceModel> SourceList { get; private set; } = new List<SourceModel>();
        public readonly string SokuModSourceTempDirPath = Path.Combine(Path.GetTempPath(), "SokuModSource");
        private readonly List<SourceConfigModel> sourceConfigs;

        public SourceManager(List<SourceConfigModel> sourceConfigs)
        {
            this.sourceConfigs = sourceConfigs;
        }

        public async Task FetchSourceList()
        {
            SourceList = sourceConfigs.Select(x => new SourceModel { Name = x.Name ?? "", Url = x.Url ?? "", PreferredDownloadLinkType = x.PreferredDownloadLinkType }).ToList();
            OnSourceManagerStatusChanged(new SourceManagerStatusChangedEventArgs
            {
                Status = SourceManagerStatus.Fetching
            });

            List<Task> tasks = new List<Task>();

            foreach (var source in SourceList)
            {
                if (source.Url == null) continue;

                tasks.Add(
                    Task.Run(async () =>
                    {
                        try
                        {
                            await FetchModuleList(source);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Error fetching modules data for {source.Name}", ex);
                        }
                    })
                );
            }
            await Task.WhenAll(tasks);
        }

        private static async Task RunTasksInBatches(List<Task> tasks, int batchSize)
        {
            for (int i = 0; i < tasks.Count; i += batchSize)
            {
                var currentBatch = tasks.Skip(i).Take(batchSize);
                await Task.WhenAll(currentBatch);
            }
        }

        private static async Task RetryOnFailure(Func<Task> task, int retryLimit = 10, int currentRetryCount = 0)
        {
            try
            {
                await task();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed", ex);
                if (currentRetryCount < retryLimit)
                {
                    await Task.Delay(1000 * (currentRetryCount + 1));
                    await RetryOnFailure(task, retryLimit, currentRetryCount + 1);
                }
                else
                {
                    throw new Exception("The retry limit has been reached.", ex);
                }
            }
        }

        public async Task<bool> FetchModuleListSnapshot(SourceModel source)
        {
            try
            {
                var modulesCacheUrl = new Uri(new Uri(source.Url), "modules-snapshot.json").ToString();
                var modulesJson = await Common.DownloadStringAsync(modulesCacheUrl);
                if (modulesJson != null)
                {
                    source.ModuleSummaries = JsonConvert.DeserializeObject<List<SourceModuleSummaryModel>>(modulesJson) ?? new List<SourceModuleSummaryModel>();
                    source.Modules = JsonConvert.DeserializeObject<List<SourceModuleModel>>(modulesJson) ?? new List<SourceModuleModel>();
                    List<Task> tasks = new List<Task>();
                    foreach (var moduleSummary in source.ModuleSummaries)
                    {
                        tasks.Add(DownloadModuleImageFiles(moduleSummary, source));
                    }
                    await Task.WhenAll(tasks);
                    return true;
                }
                else
                {
                    Logger.LogInformation($"Error fetching modules data snapshot for {source.Name}: modules.json not found or empty");
                    return false;
                }
            } 
            catch (Exception ex)
            {
                Logger.LogError($"Error fetching modules data snapshot for {source.Name}", ex);
                return false;
            }
        }

        public async Task FetchModuleList(SourceModel source, List<SourceModuleSummaryModel> specifyModuleSummaries = null)
        {
            if (specifyModuleSummaries == null)
            {
                if (await FetchModuleListSnapshot(source))
                {
                    // If snapshot is available, don't fetch modules.json
                    return;
                }

                var modulesUrl = new Uri(new Uri(source.Url), "modules.json").ToString();
                var modulesJson = await Common.DownloadStringAsync(modulesUrl);
                if (modulesJson != null)
                {
                    source.ModuleSummaries = JsonConvert.DeserializeObject<List<SourceModuleSummaryModel>>(modulesJson) ?? new List<SourceModuleSummaryModel>();
                }
                else
                {
                    Logger.LogInformation($"Error fetching modules data for {source.Name}: modules.json not found or empty");
                }
            }

            List<Task> tasks = new List<Task>();

            foreach (var moduleSummary in specifyModuleSummaries ?? source.ModuleSummaries)
            {
                _ = DownloadModuleImageFiles(moduleSummary, source);
                tasks.Add(
                    RetryOnFailure(
                        async () =>
                        {
                            try
                            {
                                var modInfoUrl = new Uri(new Uri(source.Url), $"modules/{moduleSummary.Name}/mod.json").ToString();

                                var modInfoJson = await Common.DownloadStringAsync(modInfoUrl);
                                if (modInfoJson != null)
                                {
                                    var modInfo = JsonConvert.DeserializeObject<SourceModuleModel>(modInfoJson);
                                    if (modInfo != null)
                                    {
                                        modInfo.Icon = moduleSummary.Icon;
                                        modInfo.Banner = moduleSummary.Banner;

                                        if (modInfo.RecommendedVersionNumber != null)
                                        {
                                            modInfo.RecommendedVersion = await FetchModuleVersionInfo(source, modInfo.Name, modInfo.RecommendedVersionNumber);
                                        }


                                        source.Modules.Add(modInfo);
                                    }
                                }
                                else
                                {
                                    throw new Exception("Get mod info failed.");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError($"Error fetching module data for {moduleSummary.Name}", ex);
                                throw ex;
                            }
                        }
                        )
                    );
            }

            if (source.Url.StartsWith("https://gitee.com"))
            {
                await RunTasksInBatches(tasks, 3);
            }
            else
            {
                await Task.WhenAll(tasks);
            }
        }

        private async Task DownloadModuleImageFiles(SourceModuleSummaryModel moduleSummary, SourceModel source)
        {
            try
            {
                if (source.Name == null) throw new Exception("Source name is null");
                if (source.Url == null) throw new Exception("Source URL is null");
                if (moduleSummary.Name == null) throw new Exception("Module name is null");

                string tmpSourceFolder = Path.Combine(SokuModSourceTempDirPath, source.Name);
                Directory.CreateDirectory(tmpSourceFolder);

                string tmpModuleFolder = Path.Combine(tmpSourceFolder, moduleSummary.Name);
                Directory.CreateDirectory(tmpModuleFolder);

                if (moduleSummary.Icon != null)
                {
                    await Common.DownloadAndSaveFileAsync(source.Url, $"modules/{moduleSummary.Name}/{moduleSummary.Icon}", tmpModuleFolder, moduleSummary.Icon);
                }
                if (moduleSummary.Banner != null)
                {
                    await Common.DownloadAndSaveFileAsync(source.Url, $"modules/{moduleSummary.Name}/{moduleSummary.Banner}", tmpModuleFolder, moduleSummary.Banner);
                }

            }
            catch (Exception ex)
            {
                Logger.LogError($"Error downloading module files for {moduleSummary.Name}", ex);
            }
        }

        public static async Task<SourceModuleVersionModel> FetchModuleVersionInfo(SourceModel source, string moduleName, string versionNumber)
        {
            var versionInfoUrl = new Uri(new Uri(source.Url), $"modules/{moduleName}/versions/{versionNumber}/version.json").ToString();

            var versionInfoJson = await Common.DownloadStringAsync(versionInfoUrl);
            if (versionInfoJson != null)
            {
                var versionInfo = JsonConvert.DeserializeObject<SourceModuleVersionModel>(versionInfoJson);
                return versionInfo;
            }
            return null;
        }
    }
}
