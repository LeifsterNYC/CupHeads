using System;
using System.IO;
using System.Text;
using BepInEx;

namespace CupheadOnline.Diagnostics
{
    internal static class BugReportExporter
    {
        public static string Export()
        {
            string root = Path.Combine(
                Path.Combine(Paths.BepInExRootPath, "CupheadOnline"),
                "Reports");
            Directory.CreateDirectory(root);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string reportDir = Path.Combine(root, "report-" + stamp);
            Directory.CreateDirectory(reportDir);

            File.WriteAllText(
                Path.Combine(reportDir, "diagnostics.txt"),
                BuildReportText(),
                Encoding.UTF8);

            CopyIfExists(
                Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
                Path.Combine(reportDir, "LogOutput.log"));

            CopyIfExists(
                Path.Combine(Paths.ConfigPath, PluginInfo.GUID + ".cfg"),
                Path.Combine(reportDir, PluginInfo.GUID + ".cfg"));

            return reportDir;
        }

        static string BuildReportText()
        {
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            sb.AppendLine("CupheadOnline Bug Report");
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Game Root: " + Paths.GameRootPath);
            sb.AppendLine("BepInEx Root: " + Paths.BepInExRootPath);
            sb.AppendLine("Unity Version: " + UnityEngine.Application.unityVersion);
            sb.AppendLine("Platform: " + UnityEngine.Application.platform);
            sb.AppendLine("Data Path: " + UnityEngine.Application.dataPath);
            sb.AppendLine();
            sb.AppendLine(Plugin.BuildDiagnosticsReport());
            sb.AppendLine();
            sb.AppendLine("System Info:");
            sb.AppendLine("Device: " + UnityEngine.SystemInfo.deviceModel);
            sb.AppendLine("OS: " + UnityEngine.SystemInfo.operatingSystem);
            sb.AppendLine("CPU: " + UnityEngine.SystemInfo.processorType);
            sb.AppendLine("GPU: " + UnityEngine.SystemInfo.graphicsDeviceName);
            sb.AppendLine("Memory: " + UnityEngine.SystemInfo.systemMemorySize + " MB");
            return sb.ToString().Replace("\n", nl);
        }

        static void CopyIfExists(string source, string destination)
        {
            if (!File.Exists(source))
                return;

            File.Copy(source, destination, true);
        }
    }
}
