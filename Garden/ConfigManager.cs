using System.Diagnostics;
using System.Text.Json;

namespace Garden
{
    internal class ConfigManager
    {
        private readonly Config _config;

        public double Scale => _config.scale ?? 1.0;
        public Config.WindowPositions? WindowPositions => _config.windowPositions;

        public class Config
        {
            public double? scale { get; set; }
            public WindowPositions? windowPositions { get; set; }

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
