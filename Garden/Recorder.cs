namespace Garden
{
    public abstract class Recorder
    {
        protected readonly MouseEventReporter _mouseReporter;
        protected bool _isRecording = false;

        protected Recorder(WindowType windowType)
        {
            _mouseReporter = new MouseEventReporter(windowType);
            _mouseReporter.MouseClickCallback += OnMouseClick;
            _mouseReporter.MouseMoveCallback += OnMouseMove;
        }

        public virtual void StartRecording()
        {
            if (!_isRecording)
            {
                _mouseReporter.StartReporting();
                _isRecording = true;
            }
        }

        public virtual void StopRecording()
        {
            if (_isRecording)
            {
                _mouseReporter.StopReporting();
                _isRecording = false;
            }
        }

        protected abstract void OnMouseClick(object? sender, MouseEventReporter.MouseEvent e);
        protected abstract void OnMouseMove(object? sender, MouseEventReporter.MouseEvent e);

        public void Dispose()
        {
            StopRecording();
            _mouseReporter.Dispose();
        }
    }
}
