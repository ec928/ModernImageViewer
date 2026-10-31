using System;
using System.Runtime.InteropServices;

namespace ModernImageViewer
{
    /// <summary>The standard Windows colour dialog. It has no transparency, so a chosen colour is always opaque.</summary>
    internal static class WindowsColorDialog
    {
        private const int CC_RGBINIT = 0x00000001;
        private const int CC_FULLOPEN = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ChooseColorData
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public int rgbResult;
            public IntPtr lpCustColors;
            public int Flags;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
        }

        [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW", CharSet = CharSet.Unicode)]
        private static extern bool ChooseColor(ref ChooseColorData data);

        // The dialog's sixteen custom slots, kept for as long as the app runs.
        private static readonly int[] CustomSlots = NewSlots();

        private static int[] NewSlots()
        {
            var slots = new int[16];
            Array.Fill(slots, 0x00FFFFFF);
            return slots;
        }

        /// <summary>The chosen colour as #FFRRGGBB, or null when the dialog is cancelled.</summary>
        public static string? Pick(IntPtr owner, string? current)
        {
            var start = FrameColor.Parse(current);
            var pinned = GCHandle.Alloc(CustomSlots, GCHandleType.Pinned);
            try
            {
                var data = new ChooseColorData
                {
                    lStructSize = Marshal.SizeOf<ChooseColorData>(),
                    hwndOwner = owner,
                    rgbResult = start.R | (start.G << 8) | (start.B << 16),
                    lpCustColors = pinned.AddrOfPinnedObject(),
                    Flags = CC_RGBINIT | CC_FULLOPEN
                };
                if (!ChooseColor(ref data)) return null;
                int r = data.rgbResult & 0xFF;
                int g = (data.rgbResult >> 8) & 0xFF;
                int b = (data.rgbResult >> 16) & 0xFF;
                return $"#FF{r:X2}{g:X2}{b:X2}";
            }
            finally
            {
                pinned.Free();
            }
        }
    }
}
