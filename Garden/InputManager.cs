using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NLog;

namespace Garden
{
    public class InputManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public struct Input
        {
            public int type;
            public MouseInput mouseInput;
        }

        [Flags]
        public enum InputType
        {
            Mouse = 0
        }

        [Flags]
        public enum MouseEvents
        {
            Move = 0x0001,
            LeftDown = 0x0002,
            LeftUp = 0x0004,
            VirtualDesk=0x4000,
            Absolute = 0x8000
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numInputs, Input[] inputs, int sizeInput);

        [DllImport("kernel32.dll")]
        public static extern int GetLastError();

        // Simulates a left mouse button click at the current cursor position
        // Sends both mouse down and up events simultaneously via Win32 SendInput API
        public static void Click()
        {
            Input[] inputs = new Input[1];
            inputs[0].type = (int)InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dwFlags = (uint)(MouseEvents.LeftDown | MouseEvents.LeftUp);

            uint errorCode = SendInput(1, inputs, Marshal.SizeOf(typeof(Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput error: {GetLastError()}");
            }
        }

        // Simulates a mouse button event (down or up) at the current cursor position
        public static void MouseEvent(bool isDown)
        {
            Input[] inputs = new Input[1];
            inputs[0].type = (int)InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dwFlags = (uint)(isDown ? MouseEvents.LeftDown : MouseEvents.LeftUp);

            uint errorCode = SendInput(1, inputs, Marshal.SizeOf(typeof(Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput error: {GetLastError()}");
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0; // Width of primary screen
        private const int SM_CYSCREEN = 1; // Height of primary screen
        private const int SM_CXVIRTUALSCREEN = 78; // Width of virtual screen (all monitors)
        private const int SM_CYVIRTUALSCREEN = 79; // Height of virtual screen (all monitors)
        private const int SM_XVIRTUALSCREEN = 76; // Left of virtual screen
        private const int SM_YVIRTUALSCREEN = 77; // Top of virtual screen

        // Moves the mouse cursor to the specified absolute screen coordinates
        // Uses Win32 SendInput API to simulate mouse movement
        // Parameters: x - horizontal position, y - vertical position
        public static void Move(int x, int y)
        {
            // For absolute coordinates with multi-monitor, we need to use virtual screen dimensions
            int virtualScreenWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int virtualScreenHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int virtualScreenLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualScreenTop = GetSystemMetrics(SM_YVIRTUALSCREEN);

            // Normalize to 0-65535 range based on virtual screen size
            int normalizedX = (int)((double)x * 65535 / virtualScreenWidth);
            int normalizedY = (int)((double)y * 65535 / virtualScreenHeight);

            Input[] inputs = new Input[1];
            inputs[0].type = (int)InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dx = normalizedX;
            inputs[0].mouseInput.dy = normalizedY;
            inputs[0].mouseInput.dwFlags = (uint)(MouseEvents.Move | MouseEvents.VirtualDesk | MouseEvents.Absolute);
            uint errorCode = SendInput(1, inputs, Marshal.SizeOf(typeof(Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput failed with error: {GetLastError()}");
            }
        }
    }
}
