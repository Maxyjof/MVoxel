using System.Runtime.InteropServices;
using System.Text;

namespace MVoxel.Editor;

/// <summary>
/// Win32 通用文件与文件夹选择对话框，仅编辑器 UI 调用，取消或失败返回 null
/// </summary>
internal static class NativeDialogs
{
    private const uint OfnExplorer = 0x0008_0000;
    private const uint OfnFileMustExist = 0x0000_1000;
    private const uint OfnPathMustExist = 0x0000_0800;
    private const uint OfnNoChangeDir = 0x0000_0008;
    private const uint OfnOverwritePrompt = 0x0000_0002;
    private const uint BifReturnOnlyFsDirs = 0x0000_0001;
    private const uint BifNewDialogStyle = 0x0000_0040;

    /// <summary>
    /// 弹出文件选择对话框
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <param name="filter">过滤器，形如“描述\0*.vox\0全部文件\0*.*\0”</param>
    /// <returns>选中的文件完整路径，取消时为 null</returns>
    internal static string? PickFile(string title, string filter)
    {
        var buffer = Marshal.AllocCoTaskMem(4096);
        try
        {
            Marshal.WriteInt16(buffer, 0, 0);
            var ofn = new OpenFileNameW
            {
                lStructSize = Marshal.SizeOf<OpenFileNameW>(),
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = 2048,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
            };
            if (GetOpenFileNameW(ref ofn))
            {
                var text = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// 弹出保存文件对话框
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <param name="filter">过滤器，形如“布局文件\0*.ini\0全部文件\0*.*\0”</param>
    /// <param name="defaultExtension">默认扩展名，不带点</param>
    /// <param name="suggestedName">默认文件名</param>
    /// <returns>保存目标完整路径，取消时为 null</returns>
    internal static string? SaveFile(string title, string filter, string defaultExtension, string suggestedName)
    {
        var buffer = Marshal.AllocCoTaskMem(4096);
        try
        {
            var nameBytes = Encoding.Unicode.GetBytes(suggestedName);
            Marshal.Copy(nameBytes, 0, buffer, nameBytes.Length);
            Marshal.WriteInt16(buffer, nameBytes.Length, 0);
            var ofn = new OpenFileNameW
            {
                lStructSize = Marshal.SizeOf<OpenFileNameW>(),
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = 2048,
                lpstrTitle = title,
                lpstrDefExt = defaultExtension,
                Flags = OfnExplorer | OfnPathMustExist | OfnNoChangeDir | OfnOverwritePrompt,
            };
            if (GetSaveFileNameW(ref ofn))
            {
                var text = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// 弹出文件夹选择对话框
    /// </summary>
    /// <param name="title">对话框标题</param>
    /// <returns>选中的文件夹完整路径，取消时为 null</returns>
    internal static string? PickFolder(string title)
    {
        var displayName = Marshal.AllocCoTaskMem(520);
        try
        {
            var browse = new BrowseInfoW
            {
                hwndOwner = IntPtr.Zero,
                pszDisplayName = displayName,
                lpszTitle = title,
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle,
            };
            var pidl = SHBrowseForFolderW(ref browse);
            if (pidl == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var builder = new StringBuilder(1024);
                return SHGetPathFromIDListW(pidl, builder) ? builder.ToString() : null;
            }
            finally
            {
                CoTaskMemFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(displayName);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileNameW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfoW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileNameW ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileNameW ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfoW browse);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);
}
