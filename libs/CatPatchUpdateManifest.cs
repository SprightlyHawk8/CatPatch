using System.Collections.Generic;
using Newtonsoft.Json;

namespace CatPatchspace
{
    internal sealed class CatPatchUpdateManifest
    {
        [JsonProperty("latest_version")]
        public string LatestVersion { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("package_type")]
        public string PackageType { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("changelog")]
        public List<string> Changelog { get; set; }
    }
}
