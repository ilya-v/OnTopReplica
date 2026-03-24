using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace OnTopReplica.Native {
    static class InputMethods {

        [DllImport("user32.dll")]
        static extern short GetKeyState(VirtualKeyState nVirtKey);

        const int KeyToggled = 0x1;

        const int KeyPressed = 0x8000;

        public static bool IsKeyPressed(VirtualKeyState virtKey) {
            return (GetKeyState(virtKey) & KeyPressed) != 0;
        }

        public static bool IsKeyToggled(VirtualKeyState virtKey) {
            return (GetKeyState(virtKey) & KeyToggled) != 0;
        }

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        const byte VK_MENU = 0x12; // Alt key
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// Simulates an Alt key press and release to allow SetForegroundWindow
        /// to actually bring a window to the front instead of just flashing the taskbar.
        /// </summary>
        public static void AllowSetForegroundWindowHack() {
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

    }
}
