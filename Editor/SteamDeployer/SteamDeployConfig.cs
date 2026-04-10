using UnityEngine;

namespace SteamDeployer
{
    /// <summary>
    /// ScriptableObject asset that stores all non-sensitive Steam deployment configuration.
    /// Designed to live inside an Editor folder and be committed to version control.
    /// SECURITY: Passwords and sensitive credentials are NEVER stored here.
    /// Create via: Assets > Create > SteamDeployer > Deploy Config
    /// </summary>
    [CreateAssetMenu(fileName = "SteamDeployConfig", menuName = "SteamDeployer/Deploy Config")]
    public class SteamDeployConfig : ScriptableObject
    {
        [Tooltip("Your Steam Application ID (found on the Steamworks partner portal).")]
        public string AppID = "";

        [Tooltip("Your Steam Depot ID (typically AppID + 1 for single-depot apps).")]
        public string DepotID = "";

        [Tooltip("When enabled, automatically sets the specified branch live after a successful upload. " +
                 "Disable this for new apps that have not yet passed Valve's review queue, " +
                 "or when you want to promote the build manually from the Steamworks partner portal.")]
        public bool SetLiveEnabled = false;

        [Tooltip("The Steam branch to set live after upload. Use 'default' for the main public branch, " +
                 "or a beta branch name like 'staging' or 'beta'. Only used when Set Live is enabled.")]
        public string BuildBranch = "default";

        [Tooltip("Absolute filesystem path to steamcmd.exe on this machine. " +
                 "WARNING: Path must contain only ASCII characters (no CJK, accents, etc.).")]
        public string SteamCmdPath = "";

        [Tooltip("Your Steam developer account username (the account with publishing rights for this AppID).")]
        public string SteamUsername = "";

        [Tooltip("Comma-separated glob patterns for files to exclude from the depot upload. " +
                 "Example: *.pdb, _BurstDebugInformation_DoNotShip, *.lib")]
        public string IgnoreFiles = "*.pdb, _BurstDebugInformation_DoNotShip";

        [Tooltip("Human-readable description for this build shown in the Steamworks build history. " +
                 "Supports {Version} and {Date} macro substitution (e.g., 'v{Version} built on {Date}').")]
        public string BuildDescription = "v{Version} - {Date}";
    }
}
