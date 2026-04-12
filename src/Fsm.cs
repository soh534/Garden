using NLog;
using System.Diagnostics;
using System.Text.Json;

namespace Garden
{
    public class Fsm
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private string _fsmPath;

        private Dictionary<string, Dictionary<string, string>> _transitions = new();

        public Dictionary<string, Dictionary<string, string>> Transitions => _transitions;

        public Fsm(string fsmPath)
        {
            _fsmPath = fsmPath;
            string jsonString = File.ReadAllText(fsmPath);
            _transitions = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(jsonString);
            Debug.Assert(_transitions != null);
        }
    }
}
