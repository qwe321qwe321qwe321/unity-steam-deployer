using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SteamDeployer
{
    /// <summary>
    /// Utility class responsible for dynamically generating Valve Data Format (VDF) script files
    /// required by SteamCMD to perform app builds and depot uploads.
    ///
    /// VDF FORMAT NOTES:
    ///   - VDF uses tab-indented key-value pairs enclosed in braces.
    ///   - String values are double-quoted.
    ///   - Backslashes in path strings must be doubled (\\) as VDF treats \ as an escape character.
    ///   - SteamCMD's VDF parser does NOT support Unicode; all paths must be ASCII-safe.
    ///
    /// OUTPUT FILES:
    ///   {steamcmd_dir}/scripts/app_build_{AppID}.vdf
    ///   {steamcmd_dir}/scripts/depot_build_{DepotID}.vdf
    /// </summary>
    public static class VDFGenerator
    {
        /// <summary>
        /// Generates both the app_build and depot_build VDF files, writing them to the
        /// /scripts/ subdirectory adjacent to steamcmd.exe.
        /// </summary>
        /// <param name="config">Deployment configuration (AppID, DepotID, branch, etc.).</param>
        /// <param name="buildOutputPath">Absolute path to the Unity build output folder.</param>
        /// <param name="resolvedDescription">Build description with macros already substituted.</param>
        /// <returns>Absolute path to the generated app_build VDF file.</returns>
        public static string GenerateVdfScripts(SteamDeployConfig config, string buildOutputPath, string resolvedDescription)
        {
            string steamCmdDir  = Path.GetDirectoryName(config.SteamCmdPath);
            string scriptsDir   = Path.Combine(steamCmdDir, "scripts");
            string buildLogDir  = Path.Combine(steamCmdDir, "logs");

            // Ensure both output directories exist
            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(buildLogDir);

            string depotVdfPath = WriteDepotVdf(config, scriptsDir);
            string appVdfPath   = WriteAppVdf(config, scriptsDir, buildOutputPath, buildLogDir, resolvedDescription, depotVdfPath);

            return appVdfPath;
        }

        // ─── App Build VDF ────────────────────────────────────────────────────────

        /// <summary>
        /// Generates and writes the app_build_{AppID}.vdf file.
        /// This is the top-level build script that SteamCMD executes via +run_app_build.
        /// </summary>
        private static string WriteAppVdf(
            SteamDeployConfig config,
            string scriptsDir,
            string buildOutputPath,
            string buildLogDir,
            string resolvedDescription,
            string depotVdfPath)
        {
            // VDF path strings must use double-backslashes on Windows.
            // We use Path.GetFullPath to normalize, then escape for VDF.
            string contentRoot   = EscapePathForVdf(Path.GetFullPath(buildOutputPath));
            string buildOutput   = EscapePathForVdf(Path.GetFullPath(buildLogDir));
            string depotScript   = EscapePathForVdf(Path.GetFullPath(depotVdfPath));

            var sb = new StringBuilder();
            sb.AppendLine("\"AppBuild\"");
            sb.AppendLine("{");
            sb.AppendLine($"\t\"AppID\"\t\t\"{EscapeVdfValue(config.AppID)}\"");
            sb.AppendLine($"\t\"Desc\"\t\t\"{EscapeVdfValue(resolvedDescription)}\"");
            // Silent=0 shows progress; Preview=0 means this is a real upload, not a dry-run.
            sb.AppendLine("\t\"Silent\"\t\"0\"");
            sb.AppendLine("\t\"Preview\"\t\"0\"");
            // ContentRoot: the root directory whose contents are uploaded (Unity build output).
            sb.AppendLine($"\t\"ContentRoot\"\t\"{contentRoot}\"");
            // BuildOutput: where SteamCMD writes its own build log chunks.
            sb.AppendLine($"\t\"BuildOutput\"\t\"{buildOutput}\"");
            // SetLive: the branch to promote the build to after upload. Empty string = no promotion.
            sb.AppendLine($"\t\"SetLive\"\t\"{EscapeVdfValue(config.BuildBranch)}\"");
            // Depots: maps each DepotID to its corresponding depot build script.
            sb.AppendLine("\t\"Depots\"");
            sb.AppendLine("\t{");
            sb.AppendLine($"\t\t\"{EscapeVdfValue(config.DepotID)}\"\t\"{depotScript}\"");
            sb.AppendLine("\t}");
            sb.AppendLine("}");

            string appVdfPath = Path.Combine(scriptsDir, $"app_build_{config.AppID}.vdf");
            File.WriteAllText(appVdfPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[SteamDeployer] Written app VDF: {appVdfPath}");

            return appVdfPath;
        }

        // ─── Depot Build VDF ──────────────────────────────────────────────────────

        /// <summary>
        /// Generates and writes the depot_build_{DepotID}.vdf file.
        /// This script defines file inclusion/exclusion rules for one Steam depot.
        /// The LocalPath "*" with Recursive "1" uploads ALL files from ContentRoot,
        /// then FileExclusion entries strip out debug artifacts and editor-only files.
        /// </summary>
        private static string WriteDepotVdf(SteamDeployConfig config, string scriptsDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"DepotBuild\"");
            sb.AppendLine("{");
            sb.AppendLine($"\t\"DepotID\"\t\"{EscapeVdfValue(config.DepotID)}\"");
            sb.AppendLine("\t\"FileMapping\"");
            sb.AppendLine("\t{");
            // LocalPath "*" + Recursive "1" = upload every file in ContentRoot recursively.
            sb.AppendLine("\t\t\"LocalPath\"\t\"*\"");
            // DepotPath "." maps all local files to the root of the depot (preserving subdir structure).
            sb.AppendLine("\t\t\"DepotPath\"\t\".\"");
            sb.AppendLine("\t\t\"Recursive\"\t\"1\"");
            sb.AppendLine("\t}");

            // Split the comma-separated IgnoreFiles string and emit one FileExclusion line per entry.
            // Multiple FileExclusion lines are additive (OR logic) in SteamCMD.
            if (!string.IsNullOrWhiteSpace(config.IgnoreFiles))
            {
                string[] exclusions = config.IgnoreFiles.Split(
                    new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string exclusion in exclusions)
                {
                    string pattern = exclusion.Trim();
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        sb.AppendLine($"\t\"FileExclusion\"\t\"{EscapeVdfValue(pattern)}\"");
                    }
                }
            }

            sb.AppendLine("}");

            string depotVdfPath = Path.Combine(scriptsDir, $"depot_build_{config.DepotID}.vdf");
            File.WriteAllText(depotVdfPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[SteamDeployer] Written depot VDF: {depotVdfPath}");

            return depotVdfPath;
        }

        // ─── String Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Escapes a Windows filesystem path for embedding in a VDF string value.
        /// VDF treats backslash as an escape character, so each \ must become \\.
        /// Forward slashes are NOT converted — SteamCMD accepts both on all platforms.
        /// </summary>
        private static string EscapePathForVdf(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            // Normalize to backslashes first (Windows convention), then double-escape them.
            return path.Replace("/", "\\").Replace("\\", "\\\\");
        }

        /// <summary>
        /// Escapes special characters within a generic VDF string value (non-path).
        /// Only double-quotes need escaping in VDF values; backslash is only special in paths.
        /// </summary>
        private static string EscapeVdfValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("\"", "\\\"");
        }
    }
}
