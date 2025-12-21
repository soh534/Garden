using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NLog;

namespace Garden
{
    public class InputManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Simulates a left mouse button click at the current cursor position
        // Sends both mouse down and up events simultaneously via Win32 SendInput API
        public static void Click()
        {
            Win32Api.Input[] inputs = new Win32Api.Input[1];
            inputs[0].type = (int)Win32Api.InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dwFlags = (uint)(Win32Api.MouseEvents.LeftDown | Win32Api.MouseEvents.LeftUp);

            uint errorCode = Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput error: {Win32Api.GetLastError()}");
            }
        }

        // Simulates a mouse button event (down or up) at the current cursor position
        public static void MouseEvent(bool isDown)
        {
            Win32Api.Input[] inputs = new Win32Api.Input[1];
            inputs[0].type = (int)Win32Api.InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dwFlags = (uint)(isDown ? Win32Api.MouseEvents.LeftDown : Win32Api.MouseEvents.LeftUp);

            uint errorCode = Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput error: {Win32Api.GetLastError()}");
            }
        }

        // Moves the mouse cursor to the specified absolute screen coordinates
        // Uses Win32 SendInput API to simulate mouse movement
        // Parameters: x - horizontal position, y - vertical position
        public static void Move(int x, int y)
        {
            // For absolute coordinates with multi-monitor, we need to use virtual screen dimensions
            int virtualScreenWidth = Win32Api.GetSystemMetrics(Win32Api.SM_CXVIRTUALSCREEN);
            int virtualScreenHeight = Win32Api.GetSystemMetrics(Win32Api.SM_CYVIRTUALSCREEN);
            int virtualScreenLeft = Win32Api.GetSystemMetrics(Win32Api.SM_XVIRTUALSCREEN);
            int virtualScreenTop = Win32Api.GetSystemMetrics(Win32Api.SM_YVIRTUALSCREEN);

            // Normalize to 0-65535 range based on virtual screen size
            int normalizedX = (int)((double)x * 65535 / virtualScreenWidth);
            int normalizedY = (int)((double)y * 65535 / virtualScreenHeight);

            Win32Api.Input[] inputs = new Win32Api.Input[1];
            inputs[0].type = (int)Win32Api.InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dx = normalizedX;
            inputs[0].mouseInput.dy = normalizedY;
            inputs[0].mouseInput.dwFlags = (uint)(Win32Api.MouseEvents.Move | Win32Api.MouseEvents.VirtualDesk | Win32Api.MouseEvents.Absolute);
            uint errorCode = Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.Input)));
            if (errorCode != 1)
            {
                Logger.Error($"SendInput failed with error: {Win32Api.GetLastError()}");
            }
        }
    }
}
