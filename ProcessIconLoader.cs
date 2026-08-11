using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppVolumeBoost;

internal static class ProcessIconLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? Load(string? executablePath)
    {
        var path = executablePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var result = SHGetFileInfo(path, 0, out var info,
            (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }
}
