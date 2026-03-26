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

        public double Scale => _config.scale ?? 1.0;
        public Config.WindowPositions? WindowPositions => _config.windowPositions;
        public Thirdparty GetThirdPartySdk(string name) => _config.thirdPartySdks.Find(sdk => sdk.name.Equals(name));

        public class Config
        {
            public double? scale { get; set; }
            public WindowPositions? windowPositions { get; set; }
            public required List<Thirdparty> thirdPartySdks { get; set; }

            public class WindowPositions
            {
                public Position? topLeft { get; set; }
                public int spacing { get; set; }
            }

            public class Position
            {
                public int x { get; set; }
                public int y { get; set; }
            }

            public class Thirdparty
            {
                public required string name { get; set; }
                public required string path { get; set; }
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
