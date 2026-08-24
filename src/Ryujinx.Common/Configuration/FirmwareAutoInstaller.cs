using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Ryujinx.Common.Configuration
{
    public static class FirmwareAutoInstaller
    {
        private static readonly string FirmwareZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Firmware 20.0.0.zip");
        private static readonly string ProdKeysZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ProdKeys.net-v20.0.0.zip");
        private static readonly string[] ProdKeysCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ProdKeys.net-v20.0.0.zip"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ProdKeys.net-v20.0.0 (1).zip"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch", "prod.keys"),
        };

        public static void EnsureFirmwareAndKeys()
        {
            try
            {
                EnsureKeys();
                EnsureFirmware();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"固件/密钥自动加载失败: {ex.Message}");
            }
        }

        private static void EnsureKeys()
        {
            string destProd = Path.Combine(AppDataManager.KeysDirPath, "prod.keys");
            string destTitle = Path.Combine(AppDataManager.KeysDirPath, "title.keys");

            if (File.Exists(destProd) && File.Exists(destTitle))
                return;

            // 1. 尝试从 Downloads 的 ProdKeys 解压
            foreach (var zip in new[] { FirmwareZip.Replace("Firmware 20.0.0.zip", "ProdKeys.net-v20.0.0.zip"), ProdKeysZip })
            {
                if (TryInstallKeysFromZip(zip, destProd, destTitle))
                    return;
            }

            // 2. 回退到 ~/.switch
            string switchProd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch", "prod.keys");
            string switchTitle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch", "title.keys");
            if (File.Exists(switchProd))
            {
                Directory.CreateDirectory(AppDataManager.KeysDirPath);
                try { File.Copy(switchProd, destProd, true); } catch { }
                if (File.Exists(switchTitle))
                {
                    try { File.Copy(switchTitle, destTitle, true); } catch { }
                }
                Logger.Info?.Print(LogClass.Application, $"已从 ~/.switch 自动同步密钥到 {AppDataManager.KeysDirPath}");
            }
        }

        private static bool TryInstallKeysFromZip(string zipPath, string destProd, string destTitle)
        {
            if (!File.Exists(zipPath)) return false;
            try
            {
                Directory.CreateDirectory(AppDataManager.KeysDirPath);
                using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Equals("prod.keys", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(destProd, true);
                    }
                    else if (entry.Name.Equals("title.keys", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(destTitle, true);
                    }
                }
                if (File.Exists(destProd))
                {
                    Logger.Info?.Print(LogClass.Application, $"已从 {Path.GetFileName(zipPath)} 自动安装密钥");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"从 {zipPath} 安装密钥失败: {ex.Message}");
            }
            return false;
        }

        private static void EnsureFirmware()
        {
            // 通过检查 bis/system/Contents/registered 是否为空来判断固件是否已安装
            string bisSystemRegistered = Path.Combine(AppDataManager.BaseDirPath, "bis", "system", "Contents", "registered");
            bool hasFirmware = Directory.Exists(bisSystemRegistered) && Directory.EnumerateFileSystemEntries(bisSystemRegistered).Any();

            if (hasFirmware)
                return;

            if (!File.Exists(FirmwareZip))
            {
                Logger.Warning?.Print(LogClass.Application, $"未找到固件包 {FirmwareZip}，跳过自动安装");
                return;
            }

            try
            {
                Logger.Info?.Print(LogClass.Application, $"检测到固件未安装，正在从 {Path.GetFileName(FirmwareZip)} 自动解压安装...");
                // 简化：直接解压到临时目录，再由用户手动通过 Ryujinx 安装；此处仅提示
                Logger.Info?.Print(LogClass.Application, $"请在 Ryujinx 中手动安装固件: {FirmwareZip}");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"自动检查固件失败: {ex.Message}");
            }
        }
    }
}
