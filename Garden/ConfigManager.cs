using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static Garden.ConfigManager.Config;

namespace Garden
{
    internal class ConfigManager
    {
        private readonly Config _config;

        // Expose config values as properties
        public string ImageSavePath => _config.imageSavePath;
        public Thirdparty? GetThirdPartySdk(string name) => _config.thirdPartySdks.Find(sdk => sdk.name.Equals(name));

        public class Config
        {
            public string imageSavePath { get; set; }
            public List<Thirdparty> thirdPartySdks { get; set; }

            public class Thirdparty
            {
                public string name { get; set; }
                public string path { get; set; }
            }

        }

        public ConfigManager(string configPath)
        {
            string jsonString = File.ReadAllText(configPath);
            Config? config = JsonSerializer.Deserialize<Config>(jsonString);
            Debug.Assert(config != null);
            _config = config;
        }
    }
}
