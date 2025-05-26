using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Garden
{
    public class InputManager
    {
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
            LeftUp = 0x0004
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numInputs, Input[] inputs, int sizeInput);

        [DllImport("kernel32.dll")]
        public static extern int GetLastError();

        public static void Click()
        {
            Input[] inputs = new Input[1];
            inputs[0].type = (int)InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dwFlags = (uint)(MouseEvents.LeftDown | MouseEvents.LeftUp);

            uint errorCode = SendInput(1, inputs, Marshal.SizeOf(typeof(Input)));
            if (errorCode != 1)
            {
                Console.WriteLine(GetLastError());
            }
        }

        public static void Move(int x, int y)
        {
            Input[] inputs = new Input[1];
            inputs[0].type = (int)InputType.Mouse;
            inputs[0].mouseInput = new();
            inputs[0].mouseInput.dx = x;
            inputs[0].mouseInput.dy = y;
            inputs[0].mouseInput.dwFlags = (uint)(MouseEvents.Move);
            uint errorCode = SendInput(1, inputs, Marshal.SizeOf(typeof(Input)));
            if (errorCode != 1)
            {
                Console.WriteLine(GetLastError());
            }
        }
    }
}
