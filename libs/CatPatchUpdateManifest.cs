using System.Collections.Generic;
using Newtonsoft.Json;

namespace CatPatchspace
{
    internal sealed class CatPatchUpdateManifest
    {
        [JsonProperty("latest_version")]
        public string LatestVersion { get; set; }

        [JsonProperty("changelog")]
        public List<string> Changelog { get; set; }
    }
}
