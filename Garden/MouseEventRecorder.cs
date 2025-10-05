using System.Text.Json;

namespace Garden
{
    public class MouseEventRecorder : Recorder
    {
        private readonly List<MouseEventReporter.MouseEvent> _recordedEvents;
        private readonly string _saveDirectory;

        public MouseEventRecorder(string saveDirectory) : base(WindowType.Garden)
        {
            _recordedEvents = new();
            _saveDirectory = saveDirectory;
        }

        public override void StartRecording()
        {
            if (!_isRecording)
            {
                _recordedEvents.Clear();
                base.StartRecording();
                Console.WriteLine("Mouse recording started...");
            }
            else
            {
                Console.WriteLine("Recording is already active.");
            }
        }

        public override void StopRecording()
        {
            if (_isRecording)
            {
                base.StopRecording();
                Console.WriteLine($"Mouse recording stopped. Recorded {_recordedEvents.Count} events.");
            }
        }

        public void ResetRecording()
        {
            if (_isRecording)
            {
                int previousCount = _recordedEvents.Count;
                _recordedEvents.Clear();
                Console.WriteLine($"Recording buffer cleared. Removed {previousCount} events. Recording continues...");
            }
            else
            {
                Console.WriteLine("No active recording to reset. Use 'start recording' first.");
            }
        }

        public void SaveRecording(string filename)
        {
            // Auto-end recording if still active
            if (_isRecording)
            {
                StopRecording();
            }

            if (_recordedEvents.Count == 0)
            {
                Console.WriteLine("No mouse events to save.");
                return;
            }

            Directory.CreateDirectory(_saveDirectory);
            string filePath = Path.Combine(_saveDirectory, filename);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string jsonString = JsonSerializer.Serialize(_recordedEvents, options);
            File.WriteAllText(filePath, jsonString);
            Console.WriteLine($"Mouse events saved to {filePath}");
        }

        protected override void OnMouseClick(object? sender, MouseEventReporter.MouseEvent e)
        {
            _recordedEvents.Add(e);
        }

        protected override void OnMouseMove(object? sender, MouseEventReporter.MouseEvent e)
        {
            // Ignore mouse move events for action recording
        }
    }
}
