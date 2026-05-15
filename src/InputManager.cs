using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using NLog;

namespace Garden
{
    public class InputManager
    {
        private static NetworkStream? _controlStream;
        private static int _phoneWidth;
        private static int _phoneHeight;

        public const byte TOUCH_DOWN = 0;
        public const byte TOUCH_UP   = 1;
        public const byte TOUCH_MOVE = 2;

        public static void Initialize(NetworkStream controlStream, int phoneWidth, int phoneHeight)
        {
            _controlStream = controlStream;
            _phoneWidth    = phoneWidth;
            _phoneHeight   = phoneHeight;

            // Warm up the scrcpy control handler so it's ready before the first real action.
            SendTouch(TOUCH_DOWN, phoneWidth / 2, phoneHeight / 2);
            Thread.Sleep(50);
            SendTouch(TOUCH_UP, phoneWidth / 2, phoneHeight / 2);
        }

        public static int PhoneWidth  => _phoneWidth;
        public static int PhoneHeight => _phoneHeight;

        // Coordinate space conversions between phone-native and any display resolution.
        public static (int x, int y) PhoneToDisplay(int px, int py, int displayW, int displayH)
            => (px * displayW / _phoneWidth, py * displayH / _phoneHeight);

        public static (int x, int y) DisplayToPhone(int dx, int dy, int displayW, int displayH)
            => (dx * _phoneWidth / displayW, dy * _phoneHeight / displayH);

        public static void SendTouch(byte action, int x, int y)
        {
            if (_controlStream == null) return;
            var buf = new byte[32];
            buf[0] = 0x02; // SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT
            buf[1] = action;
            WriteUInt64BE(buf, 2,  0xFFFFFFFFFFFFFFFF); // pointer id
            WriteUInt32BE(buf, 10, (uint)x);
            WriteUInt32BE(buf, 14, (uint)y);
            WriteUInt16BE(buf, 18, (ushort)_phoneWidth);
            WriteUInt16BE(buf, 20, (ushort)_phoneHeight);
            WriteUInt16BE(buf, 22, action == TOUCH_UP ? (ushort)0 : (ushort)0xFFFF); // pressure
            WriteUInt32BE(buf, 24, 0); // action button
            WriteUInt32BE(buf, 28, 0); // buttons
            _controlStream.Write(buf, 0, 32);
        }

        private static void WriteUInt64BE(byte[] b, int o, ulong v) { WriteUInt32BE(b, o, (uint)(v >> 32)); WriteUInt32BE(b, o + 4, (uint)v); }
        private static void WriteUInt32BE(byte[] b, int o, uint v)  { b[o]=(byte)(v>>24); b[o+1]=(byte)(v>>16); b[o+2]=(byte)(v>>8); b[o+3]=(byte)v; }
        private static void WriteUInt16BE(byte[] b, int o, ushort v){ b[o]=(byte)(v>>8);  b[o+1]=(byte)v; }


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
