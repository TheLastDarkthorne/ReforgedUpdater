using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReforgedUpdater.Gui
{
    internal static class NativeMethods
    {
        private const int DwmaUseImmersiveDarkMode = 20;
        private const int DwmaWindowCornerPreference = 33;
        private const int DwmaBorderColor = 34;
        private const int CornerRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Rounded corners and an outline to match the theme on Windows 11, and a dark frame
        /// in dark mode. Older versions ignore the attributes, which is the right fallback.
        /// </summary>
        public static void StyleFrame(Window window, bool dark)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int corner = CornerRound;
                DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref corner, sizeof(int));
                int darkFrame = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref darkFrame, sizeof(int));
                // COLORREF is 0x00BBGGRR: a light pink-lilac, or a deep plum in dark mode.
                int border = dark ? 0x005A3A4A : 0x00E3C8F5;
                DwmSetWindowAttribute(hwnd, DwmaBorderColor, ref border, sizeof(int));
            }
            catch { /* cosmetic only */ }
        }
    }

    /// <summary>
    /// The Explorer-style folder picker. WPF on .NET Framework has none, and WinForms'
    /// FolderBrowserDialog is the old tree view.
    /// </summary>
    internal static class FolderPicker
    {
        private const uint PickFolders = 0x20, ForceFileSystem = 0x40, NoChangeDir = 0x8;
        private const uint FileSystemPath = 0x80058000;

        public static string Pick(Window owner, string title, string initialFolder = null)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | NoChangeDir);
                dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    Guid shellItem = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref shellItem, out IShellItem folder) == 0)
                        dialog.SetFolder(folder);
                }

                IntPtr parent = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                if (dialog.Show(parent) != 0) return null; // cancelled

                dialog.GetResult(out IShellItem result);
                result.GetDisplayName(FileSystemPath, out IntPtr name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
                                                              [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
